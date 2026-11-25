using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Artists;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Catalog.Acquisition;

/// <summary>
/// Thin adapter so acquisition tests can spy on queueing without editing <see cref="ArtistReleaseQueuer"/>.
/// Production DI: <c>AddScoped&lt;ICatalogEditionQueuer&gt;(sp =&gt; new CatalogEditionQueuer(sp.GetRequiredService&lt;ArtistReleaseQueuer&gt;()))</c>.
/// </summary>
public interface ICatalogEditionQueuer
{
    Task<ReleaseQueueResult> QueueAsync(
        Guid ownerId,
        string artistName,
        IReadOnlyList<string> editionIds,
        ArtistReleaseKinds kind,
        WishlistItemOrigin origin,
        int trackBudget,
        bool requeueFailed,
        CancellationToken ct);
}

public sealed class CatalogEditionQueuer(ArtistReleaseQueuer inner) : ICatalogEditionQueuer
{
    public Task<ReleaseQueueResult> QueueAsync(
        Guid ownerId,
        string artistName,
        IReadOnlyList<string> editionIds,
        ArtistReleaseKinds kind,
        WishlistItemOrigin origin,
        int trackBudget,
        bool requeueFailed,
        CancellationToken ct) =>
        inner.QueueAsync(ownerId, artistName, editionIds, kind, origin, trackBudget, requeueFailed, ct);
}

/// <summary>
/// Availability + acquire for a catalog release. Queueing only goes through a scored Deezer edition
/// via <see cref="ICatalogEditionQueuer"/> — never because Discogs alone lists the release.
/// </summary>
public sealed class CatalogAcquisitionService(
    MusicHoarderDbContext db,
    IDeezerCatalogService deezer,
    ICatalogEditionQueuer queuer,
    IOptions<MusicEnricherOptions> options) : ICatalogAcquisitionService
{
    /// <summary>
    /// Origin used when the catalog browse queues a release. There is no dedicated "catalog" origin;
    /// <see cref="WishlistItemOrigin.ArtistRequest"/> is the person-asked bucket ("Get album").
    /// </summary>
    public const WishlistItemOrigin QueueOrigin = WishlistItemOrigin.ArtistRequest;

    /// <summary>Default kind when the release format does not say single/EP/compilation.</summary>
    public const ArtistReleaseKinds DefaultQueueKind = ArtistReleaseKinds.Album;

    private const double MinConfidence = 0.6;

    public async Task<CatalogAvailabilityDto?> GetAvailabilityAsync(int releaseId, CancellationToken ct = default)
    {
        var release = await LoadReleaseAsync(releaseId, ct);
        if (release is null)
            return null;

        var ownership = await OwnershipAsync(release, ct);
        var editions = await ScoreEditionsAsync(release, ct);
        var purchaseLinks = await PurchaseLinksAsync(release, ct);

        var trackCount = release.Tracks.Count(t => IsAudioTrack(t.Type));
        var status = ownership.OwnedSongs > 0 && trackCount > 0 && ownership.OwnedSongs >= trackCount
            ? "owned"
            : editions.Count > 0 ? "available" : "unavailable";

        return new CatalogAvailabilityDto(release.Id, status, ownership, editions, purchaseLinks);
    }

    public async Task<CatalogAcquireResultDto> AcquireAsync(
        Guid ownerId, int releaseId, string? providerEditionId, CancellationToken ct = default)
    {
        var release = await LoadReleaseAsync(releaseId, ct);
        if (release is null)
        {
            return new CatalogAcquireResultDto("not-found", 0, null, "Release not found.", []);
        }

        var ownership = await OwnershipAsync(release, ct);
        var trackCount = release.Tracks.Count(t => IsAudioTrack(t.Type));
        var purchaseLinks = await PurchaseLinksAsync(release, ct);

        if (ownership.OwnedSongs > 0 && trackCount > 0 && ownership.OwnedSongs >= trackCount)
        {
            return new CatalogAcquireResultDto("already-owned", 0, null, "Every track of this release is already owned.", purchaseLinks);
        }

        var editions = await ScoreEditionsAsync(release, ct);
        if (editions.Count == 0)
        {
            return new CatalogAcquireResultDto(
                "unavailable", 0, null,
                "No matching Deezer edition was found for this release.",
                purchaseLinks);
        }

        CatalogProviderEditionDto chosen;
        if (!string.IsNullOrWhiteSpace(providerEditionId))
        {
            var match = editions.FirstOrDefault(e =>
                string.Equals(e.EditionId, providerEditionId.Trim(), StringComparison.Ordinal));
            if (match is null)
            {
                return new CatalogAcquireResultDto(
                    "unavailable", 0, providerEditionId.Trim(),
                    "The requested provider edition is not among the scored matches.",
                    purchaseLinks);
            }
            chosen = match;
        }
        else
        {
            chosen = editions[0];
        }

        var artistName = release.ArtistDisplay
            ?? release.Credits.Where(c => c.Kind == CatalogCreditKind.Main && c.TrackSequence is null)
                .OrderBy(c => c.Sequence).Select(c => c.Name).FirstOrDefault()
            ?? "Unknown";
        var kind = KindFromRelease(release);
        var budget = Math.Max(1, options.Value.ArtistRequestMaxTracks);

        var result = await queuer.QueueAsync(
            ownerId,
            artistName,
            [chosen.EditionId],
            kind,
            QueueOrigin,
            budget,
            requeueFailed: true,
            ct);

        return new CatalogAcquireResultDto(
            "queued",
            result.Queued + result.Requeued,
            chosen.EditionId,
            result.Error,
            purchaseLinks);
    }

    private async Task<CatalogRelease?> LoadReleaseAsync(int releaseId, CancellationToken ct) =>
        await db.CatalogReleases.AsNoTracking()
            .Include(r => r.Tracks)
            .Include(r => r.Credits)
            .Include(r => r.Identifiers)
            .Include(r => r.Formats)
            .Include(r => r.Labels)
            .FirstOrDefaultAsync(r => r.Id == releaseId, ct);

    private async Task<CatalogOwnershipDto> OwnershipAsync(CatalogRelease release, CancellationToken ct)
    {
        var ownedSongs = await db.Songs.AsNoTracking()
            .CountAsync(s => s.DeletedAtUtc == null && s.DiscogsReleaseId == release.DiscogsId, ct);
        return new CatalogOwnershipDto(ownedSongs, ownedSongs > 0 ? 1 : 0);
    }

    private async Task<IReadOnlyList<CatalogProviderEditionDto>> ScoreEditionsAsync(
        CatalogRelease release, CancellationToken ct)
    {
        var scored = new List<CatalogProviderEditionDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var linked = await db.CatalogExternalIds.AsNoTracking()
            .Where(e => e.EntityKind == CatalogEntityKind.Release
                        && e.EntityId == release.Id
                        && e.Provider.ToLower() == "deezer")
            .Select(e => e.ExternalId)
            .ToListAsync(ct);

        foreach (var id in linked.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            if (!seen.Add(id))
                continue;
            var detail = await deezer.GetAlbumAsync(id, ct);
            var edition = ScoreDetail(release, id, detail);
            if (edition is not null && edition.Confidence >= MinConfidence)
                scored.Add(edition);
        }

        if (scored.Count == 0)
        {
            var artist = release.ArtistDisplay
                ?? release.Credits.Where(c => c.Kind == CatalogCreditKind.Main && c.TrackSequence is null)
                    .OrderBy(c => c.Sequence).Select(c => c.Name).FirstOrDefault()
                ?? "";
            var candidates = await deezer.SearchAlbumCandidatesAsync(artist, release.Title, ct);
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate.Id) || !seen.Add(candidate.Id))
                    continue;
                var detail = await deezer.GetAlbumAsync(candidate.Id, ct);
                var edition = ScoreDetail(release, candidate.Id, detail, candidate.Title, candidate.ArtistName);
                if (edition is not null && edition.Confidence >= MinConfidence)
                    scored.Add(edition);
            }
        }

        return scored.OrderByDescending(e => e.Confidence).ThenBy(e => e.EditionId, StringComparer.Ordinal).ToList();
    }

    private CatalogProviderEditionDto? ScoreDetail(
        CatalogRelease release,
        string editionId,
        DeezerAlbumDetail? detail,
        string? searchTitle = null,
        string? searchArtist = null)
    {
        var title = detail?.Title ?? searchTitle;
        var artist = detail?.Artist ?? searchArtist;
        var trackCount = detail?.Tracks.Count;
        var year = detail?.Year;
        // DeezerAlbumDetail does not expose UPC today; leave null (no UPC boost).
        string? upc = null;

        var confidence = Score(release, title, artist, trackCount, year, upc);
        return new CatalogProviderEditionDto(
            "Deezer",
            editionId,
            title ?? release.Title,
            artist,
            trackCount,
            year,
            upc,
            confidence,
            $"https://www.deezer.com/album/{editionId}");
    }

    internal static double Score(
        CatalogRelease release,
        string? candidateTitle,
        string? candidateArtist,
        int? candidateTrackCount,
        int? candidateYear,
        string? candidateUpc)
    {
        var score = 0.0;
        var releaseTitle = CatalogKeys.TitleKey(release.Title);
        var candTitle = CatalogKeys.TitleKey(candidateTitle);
        if (releaseTitle.Length > 0 && releaseTitle == candTitle)
            score += 0.35;

        var releaseArtist = CatalogKeys.NameKey(release.ArtistDisplay ?? release.ArtistKey);
        var candArtist = CatalogKeys.NameKey(candidateArtist);
        if (releaseArtist.Length > 0 && releaseArtist == candArtist)
            score += 0.25;

        var releaseTracks = release.Tracks.Count(t => IsAudioTrack(t.Type));
        if (candidateTrackCount is int tc && releaseTracks > 0 && tc == releaseTracks)
            score += 0.15;

        if (candidateYear is int y && release.Year is int ry && y == ry)
            score += 0.10;

        var barcodes = release.Identifiers
            .Where(i => i.Type.Contains("Barcode", StringComparison.OrdinalIgnoreCase)
                        || i.Type.Equals("UPC", StringComparison.OrdinalIgnoreCase)
                        || i.Type.Equals("EAN", StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Value)
            .Where(CatalogKeys.IsUsableBarcode)
            .Select(NormalizeBarcode)
            .Where(b => b.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var candBarcode = NormalizeBarcode(candidateUpc);
        if (candBarcode.Length > 0 && barcodes.Contains(candBarcode))
            score += 0.15;

        return Math.Clamp(score, 0.0, 1.0);
    }

    private async Task<IReadOnlyList<CatalogPurchaseLinkDto>> PurchaseLinksAsync(
        CatalogRelease release, CancellationToken ct)
    {
        var urls = new List<string?>();

        var releaseUrls = await db.CatalogUrls.AsNoTracking()
            .Where(u => u.EntityKind == CatalogEntityKind.Release && u.EntityId == release.Id)
            .OrderBy(u => u.Sequence)
            .Select(u => u.Url)
            .ToListAsync(ct);
        urls.AddRange(releaseUrls);

        if (release.MasterId is int masterId)
        {
            var masterUrls = await db.CatalogUrls.AsNoTracking()
                .Where(u => u.EntityKind == CatalogEntityKind.Master && u.EntityId == masterId)
                .OrderBy(u => u.Sequence)
                .Select(u => u.Url)
                .ToListAsync(ct);
            urls.AddRange(masterUrls);
        }

        var mainArtistDiscogsIds = release.Credits
            .Where(c => c.Kind == CatalogCreditKind.Main && c.TrackSequence is null)
            .Select(c => c.ArtistDiscogsId)
            .Distinct()
            .ToList();
        if (mainArtistDiscogsIds.Count > 0)
        {
            var artistIds = await db.CatalogArtists.AsNoTracking()
                .Where(a => mainArtistDiscogsIds.Contains(a.DiscogsId))
                .Select(a => a.Id)
                .ToListAsync(ct);
            if (artistIds.Count > 0)
            {
                var artistUrls = await db.CatalogUrls.AsNoTracking()
                    .Where(u => u.EntityKind == CatalogEntityKind.Artist && artistIds.Contains(u.EntityId))
                    .Select(u => u.Url)
                    .ToListAsync(ct);
                urls.AddRange(artistUrls);
            }
        }

        var labelDiscogsIds = release.Labels
            .Where(l => l.Kind == CatalogReleaseLabelKind.Label)
            .Select(l => l.LabelDiscogsId)
            .Distinct()
            .ToList();
        if (labelDiscogsIds.Count > 0)
        {
            var labelIds = await db.CatalogLabels.AsNoTracking()
                .Where(l => labelDiscogsIds.Contains(l.DiscogsId))
                .Select(l => l.Id)
                .ToListAsync(ct);
            if (labelIds.Count > 0)
            {
                var labelUrls = await db.CatalogUrls.AsNoTracking()
                    .Where(u => u.EntityKind == CatalogEntityKind.Label && labelIds.Contains(u.EntityId))
                    .Select(u => u.Url)
                    .ToListAsync(ct);
                urls.AddRange(labelUrls);
            }
        }

        return CatalogPurchaseLinks.Build(release.DiscogsId, urls);
    }

    private static ArtistReleaseKinds KindFromRelease(CatalogRelease release)
    {
        var summary = (release.FormatSummary ?? string.Empty).ToLowerInvariant();
        foreach (var format in release.Formats)
        {
            var blob = $"{format.Name} {format.Text} {format.DescriptionsJson}".ToLowerInvariant();
            if (blob.Contains("single", StringComparison.Ordinal))
                return ArtistReleaseKinds.Single;
            if (blob.Contains("ep", StringComparison.Ordinal) || summary.Contains("ep", StringComparison.Ordinal))
                return ArtistReleaseKinds.Ep;
            if (blob.Contains("compilation", StringComparison.Ordinal) || blob.Contains("compile", StringComparison.Ordinal))
                return ArtistReleaseKinds.Compilation;
        }

        if (summary.Contains("single", StringComparison.Ordinal))
            return ArtistReleaseKinds.Single;
        if (summary.Contains(" ep", StringComparison.Ordinal) || summary.StartsWith("ep", StringComparison.Ordinal))
            return ArtistReleaseKinds.Ep;
        if (summary.Contains("compilation", StringComparison.Ordinal))
            return ArtistReleaseKinds.Compilation;
        return DefaultQueueKind;
    }

    private static bool IsAudioTrack(string? type) =>
        string.IsNullOrWhiteSpace(type)
        || type.Equals("track", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeBarcode(string? value)
    {
        if (!CatalogKeys.IsUsableBarcode(value))
            return string.Empty;
        return new string(value!.Where(char.IsLetterOrDigit).ToArray());
    }
}
