using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>
/// Discogs as a supporting consensus source (<see cref="FieldAuthority"/>): it finds the song's album
/// release and offers its catalog fields (label, catalog number, barcode, styles as genre, Discogs ids).
/// Consensus never counts it as a vote; it only overlays a Matched winner of the same album. The
/// release it reads is also written into the catalog graph, so enrichment grows the browsable catalog.
/// Off unless <see cref="DiscogsOptions.EnableEnrichmentProvider"/>; searching needs a token.
/// </summary>
public sealed class DiscogsEnrichmentProvider(
    DiscogsReleaseMatcher matcher,
    IDiscogsClient client,
    IOptionsMonitor<DiscogsOptions> options,
    IServiceScopeFactory scopeFactory,
    IMemoryCache cache,
    ILogger<DiscogsEnrichmentProvider> logger) : IEnrichmentProvider
{
    /// <summary>Below this the candidate is kept for review history but laid over nothing.</summary>
    public const double MatchThreshold = 0.8;

    /// <summary>
    /// Requests left in the allowance at or below which a song skips Discogs rather than queue on the
    /// client's request gate or its 429 retries. Discogs counts the allowance over a moving minute.
    /// </summary>
    public const int RateLimitReserve = 5;

    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromSeconds(60);

    public string Name => nameof(EnrichmentProvider.Discogs);

    /// <summary>After every voting provider: nothing it finds changes what they search for.</summary>
    public int Priority => 900;

    public bool IsEnabled
        => options.CurrentValue is { Enabled: true, EnableEnrichmentProvider: true } && client.IsEnabled;

    public bool CanHandle(SongMetadata song)
        => !string.IsNullOrWhiteSpace(song.Artist)
            && !string.IsNullOrWhiteSpace(song.Album)
            && (client.IsAuthenticated || song.DiscogsReleaseId is not null);

    public async Task<ProviderOutcome> TryEnrichAsync(SongMetadata song, CancellationToken ct = default)
    {
        var albumArtist = string.IsNullOrWhiteSpace(song.AlbumArtist) ? song.Artist : song.AlbumArtist;
        if (string.IsNullOrWhiteSpace(albumArtist) || string.IsNullOrWhiteSpace(song.Album))
            return new ProviderNoMatch();

        if (client.LastRateLimit is { Remaining: { } remaining } snapshot && remaining <= RateLimitReserve)
        {
            var windowLeft = snapshot.ObservedAtUtc + RateLimitWindow - DateTime.UtcNow;
            if (windowLeft > TimeSpan.Zero)
                return new ProviderRateLimited(windowLeft < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : windowLeft);
        }

        DiscogsAlbumMatch? match;
        try
        {
            match = await matcher.MatchAsync(
                new DiscogsAlbumHints(albumArtist, song.Album, song.DiscogsReleaseId, song.Upc, song.Year, song.TotalTracks), ct);
        }
        catch (ProviderRateLimitedException ex)
        {
            return new ProviderRateLimited(ex.RetryAfter);
        }

        if (match is null)
            return new ProviderNoMatch();

        await IngestAsync(match, ct);

        var result = ToResult(song, match);
        return result.RecommendedStatus == EnrichmentStatus.Matched
            ? new ProviderMatched(result)
            : new ProviderNoMatch(result);
    }

    internal static EnrichmentProviderResult ToResult(SongMetadata song, DiscogsAlbumMatch match)
    {
        var release = match.Release.Value;
        var master = match.Master?.Value;
        var isVarious = release.Artists?.Any(a => a.Id == DiscogsReleaseMatcher.VariousArtistsId) == true;
        var lead = DiscogsReleaseMatcher.AlbumArtist(release.Artists, song.AlbumArtist ?? song.Artist);
        var artist = isVarious
            ? song.Artist
            : DiscogsReleaseMatcher.DisplayName(lead?.Name);
        var albumArtist = isVarious ? "Various Artists" : artist;
        var label = match.EditionExact ? release.Labels?.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Name)) : null;
        var catalogNumber = label?.Catno is { } catno && !string.Equals(catno.Trim(), "none", StringComparison.OrdinalIgnoreCase)
            ? catno.Trim()
            : null;
        var barcode = match.EditionExact
            ? release.Identifiers?
                .Where(i => string.Equals(i.Type, "Barcode", StringComparison.OrdinalIgnoreCase))
                .Select(i => DiscogsReleaseMatcher.UsableBarcode(i.Value))
                .FirstOrDefault(b => b is not null)
            : null;
        var styles = master?.Styles is { Count: > 0 } masterStyles ? masterStyles : release.Styles;

        var titleKey = TitleNormalizer.NormalizeForSearch(song.Title);
        var track = DiscogsTracklist.Flatten(release.Tracklist)
            .FirstOrDefault(t => titleKey.Length > 0 && TitleNormalizer.NormalizeForSearch(t.Title) == titleKey);

        var warnings = new List<string>();
        if (track is null)
            warnings.Add("track_not_on_release");
        var confidence = track is null ? match.Confidence - 0.1 : match.Confidence;

        return new EnrichmentProviderResult(
            Artist: artist,
            AlbumArtist: albumArtist,
            Title: track?.Title ?? song.Title,
            Year: master?.Year ?? release.Year,
            TrackNumber: null,
            MusicBrainzId: null,
            MusicBrainzReleaseId: null,
            SpotifyId: null,
            AcoustIdTrackId: null,
            Isrc: null,
            MatchedBy: $"Discogs:{match.Method}",
            MatchConfidence: confidence,
            MatchWarnings: warnings,
            RecommendedStatus: confidence >= MatchThreshold ? EnrichmentStatus.Matched : EnrichmentStatus.NeedsReview,
            Album: release.Title,
            Genre: Metadata.MultiValue.Join(styles ?? []),
            Label: DiscogsReleaseMatcher.DisplayName(label?.Name),
            CatalogNumber: catalogNumber,
            Upc: barcode,
            DiscogsReleaseId: match.EditionExact ? release.Id : null,
            DiscogsMasterId: release.MasterId,
            DiscogsArtistId: isVarious ? DiscogsReleaseMatcher.VariousArtistsId : lead?.Id);
    }

    /// <summary>Best effort: the catalog is a by-product here, never a reason to fail enrichment.</summary>
    private async Task IngestAsync(DiscogsAlbumMatch match, CancellationToken ct)
    {
        var key = $"discogs:ingested:{match.Release.Value.Id}";
        if (cache.TryGetValue(key, out _))
            return;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<ICatalogIngestor>() is not { } ingestor)
                return;
            if (match.Master is { } master)
                await ingestor.UpsertMasterAsync(master, ct);
            await ingestor.UpsertReleaseAsync(match.Release, ct);
            cache.Set(key, true, TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.CacheMinutes)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Discogs: could not write release {ReleaseId} to the catalog", match.Release.Value.Id);
        }
    }
}
