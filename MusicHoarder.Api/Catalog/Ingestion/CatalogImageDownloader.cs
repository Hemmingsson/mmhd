using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Catalog.Ingestion;

/// <summary>Downloads <see cref="CatalogImageStatus.Remote"/> images into Discogs:ImageDirectory.</summary>
public sealed class CatalogImageDownloader(
    IDiscogsClient client,
    MusicHoarderDbContext db,
    TimeProvider time,
    IOptionsMonitor<DiscogsOptions> options,
    ILogger<CatalogImageDownloader> logger) : ICatalogImageDownloader
{
    public async Task<int> DownloadPendingAsync(int maxImages, CancellationToken ct = default)
    {
        if (!options.CurrentValue.DownloadImages || maxImages <= 0)
            return 0;

        var pending = await db.CatalogImages
            .Where(i => i.Status == CatalogImageStatus.Remote)
            .OrderBy(i => i.SeenAtUtc)
            .ThenBy(i => i.Id)
            .Take(maxImages)
            .ToListAsync(ct);

        if (pending.Count == 0)
            return 0;

        var root = options.CurrentValue.ImageDirectory;
        Directory.CreateDirectory(root);

        var attempted = 0;
        foreach (var image in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DownloadOneAsync(image, root, ct);
            }
            catch (ProviderRateLimitedException)
            {
                // The rate-limited image stays Remote for a later pass; keep what this pass finished.
                await db.SaveChangesAsync(ct);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to download catalog image {ImageId}", image.Id);
                Fail(image, ex.Message);
            }

            attempted++;
        }

        await db.SaveChangesAsync(ct);
        return attempted;
    }

    private async Task DownloadOneAsync(CatalogImage image, string root, CancellationToken ct)
    {
        var bytes = await client.DownloadImageAsync(image.SourceUrl, ct);
        if (bytes is null || bytes.Content.Length == 0)
        {
            Fail(image, "Empty image response");
            return;
        }

        var checksum = Convert.ToHexString(SHA256.HashData(bytes.Content)).ToLowerInvariant();
        var ext = ExtensionFor(bytes.ContentType, image.SourceUrl);
        var discogsId = await ResolveDiscogsIdAsync(image.EntityKind, image.EntityId, ct);
        var kind = image.EntityKind.ToString().ToLowerInvariant();
        var relative = Path.Combine(kind, discogsId.ToString(), $"{checksum}{ext}");
        var absolute = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        var temp = absolute + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes.Content, ct);
        File.Move(temp, absolute, overwrite: true);

        image.Checksum = checksum;
        image.LocalPath = relative.Replace('\\', '/');
        image.ContentType = bytes.ContentType;
        image.ByteSize = bytes.Content.Length;
        image.Status = CatalogImageStatus.Downloaded;
        image.DownloadedAtUtc = time.GetUtcNow().UtcDateTime;
        image.LastError = null;
    }

    private void Fail(CatalogImage image, string error)
    {
        image.Status = CatalogImageStatus.Failed;
        image.LastError = error.Length <= 1024 ? error : error[..1024];
        image.DownloadedAtUtc = time.GetUtcNow().UtcDateTime;
    }

    private async Task<long> ResolveDiscogsIdAsync(CatalogEntityKind kind, int entityId, CancellationToken ct) =>
        kind switch
        {
            CatalogEntityKind.Artist => (await db.CatalogArtists.Where(a => a.Id == entityId).Select(a => a.DiscogsId).FirstAsync(ct)),
            CatalogEntityKind.Label => (await db.CatalogLabels.Where(l => l.Id == entityId).Select(l => l.DiscogsId).FirstAsync(ct)),
            CatalogEntityKind.Master => (await db.CatalogMasters.Where(m => m.Id == entityId).Select(m => m.DiscogsId).FirstAsync(ct)),
            CatalogEntityKind.Release => (await db.CatalogReleases.Where(r => r.Id == entityId).Select(r => r.DiscogsId).FirstAsync(ct)),
            _ => entityId,
        };

    private static string ExtensionFor(string? contentType, string sourceUrl)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var mime = contentType.Split(';')[0].Trim().ToLowerInvariant();
            return mime switch
            {
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/png" => ".png",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                _ => ".bin",
            };
        }

        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            var ext = Path.GetExtension(uri.AbsolutePath);
            if (!string.IsNullOrEmpty(ext) && ext.Length <= 8)
                return ext.ToLowerInvariant();
        }

        return ".jpg";
    }
}
