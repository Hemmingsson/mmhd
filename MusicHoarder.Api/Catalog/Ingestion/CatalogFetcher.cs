using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Catalog.Ingestion;

/// <summary>
/// Ensures catalog resources are present and fresh. Listing fetches page at 100/page, capped at 20
/// pages (≤20 listing requests + 1 document request per Ensure* with listing).
/// </summary>
public sealed class CatalogFetcher(
    IDiscogsClient client,
    ICatalogIngestor ingestor,
    MusicHoarderDbContext db,
    TimeProvider time,
    IOptionsMonitor<DiscogsOptions> options,
    ILogger<CatalogFetcher> logger) : ICatalogFetcher
{
    public const int ListingPageSize = 100;
    public const int MaxListingPages = 20;

    public async Task<CatalogArtist?> EnsureArtistAsync(long discogsId, bool withDiscography, bool force = false, CancellationToken ct = default)
    {
        var existing = await db.CatalogArtists.FirstOrDefaultAsync(a => a.DiscogsId == discogsId, ct);
        if (!force && IsFresh(existing) && (!withDiscography || existing!.DiscographyFetchedAtUtc.HasValue))
            return existing;

        if (!client.IsEnabled || !options.CurrentValue.Enabled)
            return existing;

        DiscogsDocument<DiscogsArtist>? document;
        try
        {
            document = await client.GetArtistAsync(discogsId, ct);
        }
        catch (DiscogsApiException ex) when (ex.StatusCode == 404)
        {
            await ingestor.MarkUnavailableAsync(CatalogEntityKind.Artist, discogsId, null, ct);
            return null;
        }

        if (document is null)
        {
            await ingestor.MarkUnavailableAsync(CatalogEntityKind.Artist, discogsId, null, ct);
            return null;
        }

        var artist = await ingestor.UpsertArtistAsync(document, ct);

        if (withDiscography)
        {
            var (entries, complete) = await PageArtistReleasesAsync(discogsId, ct);
            await ingestor.UpsertArtistDiscographyAsync(artist.Id, entries, complete, ct);
        }

        return artist;
    }

    public async Task<CatalogLabel?> EnsureLabelAsync(long discogsId, bool withReleases, bool force = false, CancellationToken ct = default)
    {
        var existing = await db.CatalogLabels.FirstOrDefaultAsync(l => l.DiscogsId == discogsId, ct);
        if (!force && IsFresh(existing) && (!withReleases || existing!.ReleasesFetchedAtUtc.HasValue))
            return existing;

        if (!client.IsEnabled || !options.CurrentValue.Enabled)
            return existing;

        DiscogsDocument<DiscogsLabel>? document;
        try
        {
            document = await client.GetLabelAsync(discogsId, ct);
        }
        catch (DiscogsApiException ex) when (ex.StatusCode == 404)
        {
            await ingestor.MarkUnavailableAsync(CatalogEntityKind.Label, discogsId, null, ct);
            return null;
        }

        if (document is null)
        {
            await ingestor.MarkUnavailableAsync(CatalogEntityKind.Label, discogsId, null, ct);
            return null;
        }

        var label = await ingestor.UpsertLabelAsync(document, ct);

        if (withReleases)
        {
            var (entries, complete) = await PageLabelReleasesAsync(discogsId, ct);
            await ingestor.UpsertLabelReleasesAsync(label.Id, entries, complete, ct);
        }

        return label;
    }

    public async Task<CatalogMaster?> EnsureMasterAsync(long discogsId, bool withVersions, bool force = false, CancellationToken ct = default)
    {
        var existing = await db.CatalogMasters.FirstOrDefaultAsync(m => m.DiscogsId == discogsId, ct);
        if (!force && IsFresh(existing) && (!withVersions || existing!.VersionsFetchedAtUtc.HasValue))
            return existing;

        if (!client.IsEnabled || !options.CurrentValue.Enabled)
            return existing;

        DiscogsDocument<DiscogsMaster>? document;
        try
        {
            document = await client.GetMasterAsync(discogsId, ct);
        }
        catch (DiscogsApiException ex) when (ex.StatusCode == 404)
        {
            await ingestor.MarkUnavailableAsync(CatalogEntityKind.Master, discogsId, null, ct);
            return null;
        }

        if (document is null)
        {
            await ingestor.MarkUnavailableAsync(CatalogEntityKind.Master, discogsId, null, ct);
            return null;
        }

        var master = await ingestor.UpsertMasterAsync(document, ct);

        if (withVersions)
        {
            var (versions, complete) = await PageMasterVersionsAsync(discogsId, ct);
            await ingestor.UpsertMasterVersionsAsync(master.Id, versions, complete, ct);
        }

        return master;
    }

    public async Task<CatalogRelease?> EnsureReleaseAsync(long discogsId, bool force = false, CancellationToken ct = default)
    {
        var existing = await db.CatalogReleases.FirstOrDefaultAsync(r => r.DiscogsId == discogsId, ct);
        if (!force && IsFresh(existing))
            return existing;

        if (!client.IsEnabled || !options.CurrentValue.Enabled)
            return existing;

        DiscogsDocument<DiscogsRelease>? document;
        try
        {
            document = await client.GetReleaseAsync(discogsId, ct);
        }
        catch (DiscogsApiException ex) when (ex.StatusCode == 404)
        {
            await ingestor.MarkUnavailableAsync(CatalogEntityKind.Release, discogsId, null, ct);
            return null;
        }

        if (document is null)
        {
            await ingestor.MarkUnavailableAsync(CatalogEntityKind.Release, discogsId, null, ct);
            return null;
        }

        var release = await ingestor.UpsertReleaseAsync(document, ct);

        if (release.MasterDiscogsId is long masterId and > 0)
            await EnsureMasterAsync(masterId, withVersions: false, force: false, ct);

        return release;
    }

    private bool IsFresh(CatalogResource? row)
    {
        if (row is null) return false;
        if (row.FetchState != CatalogFetchState.Fetched) return false;
        if (row.RefreshAfterUtc is not DateTime refresh) return false;
        return refresh > time.GetUtcNow().UtcDateTime;
    }

    private async Task<(List<DiscogsArtistReleaseEntry> Entries, bool Complete)> PageArtistReleasesAsync(long artistId, CancellationToken ct)
    {
        var all = new List<DiscogsArtistReleaseEntry>();
        var complete = true;
        for (var page = 1; page <= MaxListingPages; page++)
        {
            var result = await client.GetArtistReleasesAsync(artistId, page, ListingPageSize, ct);
            if (result is null) break;
            all.AddRange(result.Items);
            if (page >= result.Pagination.Pages) return (all, true);
            if (page == MaxListingPages)
            {
                complete = false;
                logger.LogWarning(
                    "Artist {ArtistId} discography capped at {Pages} pages ({Count} entries; ~{Requests} listing requests)",
                    artistId, MaxListingPages, all.Count, MaxListingPages);
            }
        }

        return (all, complete);
    }

    private async Task<(List<DiscogsLabelReleaseEntry> Entries, bool Complete)> PageLabelReleasesAsync(long labelId, CancellationToken ct)
    {
        var all = new List<DiscogsLabelReleaseEntry>();
        var complete = true;
        for (var page = 1; page <= MaxListingPages; page++)
        {
            var result = await client.GetLabelReleasesAsync(labelId, page, ListingPageSize, ct);
            if (result is null) break;
            all.AddRange(result.Items);
            if (page >= result.Pagination.Pages) return (all, true);
            if (page == MaxListingPages)
            {
                complete = false;
                logger.LogWarning(
                    "Label {LabelId} releases capped at {Pages} pages ({Count} entries; ~{Requests} listing requests)",
                    labelId, MaxListingPages, all.Count, MaxListingPages);
            }
        }

        return (all, complete);
    }

    private async Task<(List<DiscogsMasterVersion> Versions, bool Complete)> PageMasterVersionsAsync(long masterId, CancellationToken ct)
    {
        var all = new List<DiscogsMasterVersion>();
        var complete = true;
        for (var page = 1; page <= MaxListingPages; page++)
        {
            var result = await client.GetMasterVersionsAsync(masterId, page, ListingPageSize, ct: ct);
            if (result is null) break;
            all.AddRange(result.Items);
            if (page >= result.Pagination.Pages) return (all, true);
            if (page == MaxListingPages)
            {
                complete = false;
                logger.LogWarning(
                    "Master {MasterId} versions capped at {Pages} pages ({Count} entries; ~{Requests} listing requests)",
                    masterId, MaxListingPages, all.Count, MaxListingPages);
            }
        }

        return (all, complete);
    }
}
