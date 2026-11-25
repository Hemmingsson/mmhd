using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Catalog.Ingestion;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Catalog.Ingestion;

public class CatalogImageDownloaderTests : IDisposable
{
    private readonly string _imageDir = Path.Combine(Path.GetTempPath(), "mh-img-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (!Directory.Exists(_imageDir))
            return;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "trash",
                ArgumentList = { _imageDir },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch
        {
            // Temp dir left behind if trash is unavailable.
        }
    }

    [Fact]
    public async Task DownloadPending_Writes_File_Checksum_And_Marks_Downloaded()
    {
        await using var db = CatalogIngestionTestKit.NewContext();
        var clock = CatalogIngestionTestKit.Clock();
        var opts = CatalogIngestionTestKit.Options(o =>
        {
            o.DownloadImages = true;
            o.ImageDirectory = _imageDir;
        });
        var ingestor = new CatalogIngestor(db, clock, CatalogIngestionTestKit.Monitor(opts));
        var artist = await ingestor.UpsertArtistAsync(CatalogIngestionTestKit.Document<DiscogsArtist>("artist-45-aphex-twin.json"));

        var images = await db.CatalogImages
            .Where(i => i.EntityKind == CatalogEntityKind.Artist && i.EntityId == artist.Id && i.Status == CatalogImageStatus.Remote)
            .OrderBy(i => i.SeenAtUtc).ThenBy(i => i.Id)
            .ToListAsync();
        Assert.NotEmpty(images);

        var payload = "fake-jpeg-bytes"u8.ToArray();
        var client = new FakeDiscogsClient();
        foreach (var row in images)
            client.Images[row.SourceUrl] = new DiscogsImageBytes(payload, "image/jpeg");

        var downloader = new CatalogImageDownloader(client, db, clock, CatalogIngestionTestKit.Monitor(opts),
            NullLogger<CatalogImageDownloader>.Instance);

        var count = await downloader.DownloadPendingAsync(1);
        Assert.Equal(1, count);

        var image = await db.CatalogImages.FirstAsync(i => i.Id == images[0].Id);
        Assert.Equal(CatalogImageStatus.Downloaded, image.Status);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), image.Checksum);
        Assert.Equal(payload.Length, image.ByteSize);
        Assert.NotNull(image.LocalPath);
        Assert.True(File.Exists(Path.Combine(_imageDir, image.LocalPath!)));
        Assert.Equal(1, client.ImageDownloadCalls);
    }

    [Fact]
    public async Task DownloadPending_NoOp_When_Disabled()
    {
        await using var db = CatalogIngestionTestKit.NewContext();
        var clock = CatalogIngestionTestKit.Clock();
        var opts = CatalogIngestionTestKit.Options(o =>
        {
            o.DownloadImages = false;
            o.ImageDirectory = _imageDir;
        });
        var ingestor = new CatalogIngestor(db, clock, CatalogIngestionTestKit.Monitor(opts));
        await ingestor.UpsertArtistAsync(CatalogIngestionTestKit.Document<DiscogsArtist>("artist-45-aphex-twin.json"));

        var client = new FakeDiscogsClient();
        var downloader = new CatalogImageDownloader(client, db, clock, CatalogIngestionTestKit.Monitor(opts),
            NullLogger<CatalogImageDownloader>.Instance);

        Assert.Equal(0, await downloader.DownloadPendingAsync(10));
        Assert.Equal(0, client.ImageDownloadCalls);
    }
}
