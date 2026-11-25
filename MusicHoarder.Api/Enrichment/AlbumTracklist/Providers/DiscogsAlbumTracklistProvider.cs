using System.Globalization;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.AlbumTracklist.Providers;

/// <summary>
/// Album tracklists from Discogs: the master's main release, found by artist + album. A supporting
/// candidate for <see cref="AlbumTracklistReconciler"/>: it never votes on the edition, the slots or a
/// track's contest, and only corroborates tracks the voting providers already list.
/// Off unless <see cref="DiscogsOptions.EnableTracklistProvider"/>; needs a token to search.
/// </summary>
public sealed class DiscogsAlbumTracklistProvider(
    DiscogsReleaseMatcher matcher,
    IDiscogsClient client,
    IOptionsMonitor<DiscogsOptions> options) : IAlbumTracklistProvider
{
    public EnrichmentProvider Source => EnrichmentProvider.Discogs;

    public bool IsEnabled(MusicEnricherOptions _)
        => options.CurrentValue is { Enabled: true, EnableTracklistProvider: true } && client.IsAuthenticated;

    public async Task<AlbumTracklistCandidate?> FetchAsync(AlbumQuery query, CancellationToken ct = default)
    {
        var match = await matcher.MatchAsync(
            new DiscogsAlbumHints(query.AlbumArtist, query.Album, TrackCount: query.TotalTracksHint), ct);
        if (match is null || match.Confidence < DiscogsEnrichmentProvider.MatchThreshold)
            return null;

        var release = match.Release.Value;
        var tracks = DiscogsTracklist.Flatten(release.Tracklist);
        if (tracks.Count == 0)
            return null;

        var lead = (release.Artists ?? []).FirstOrDefault(a => a.Id != DiscogsReleaseMatcher.VariousArtistsId);
        return new AlbumTracklistCandidate(
            Source,
            release.Id.ToString(CultureInfo.InvariantCulture),
            release.Title,
            DiscogsReleaseMatcher.DisplayName(lead?.Name),
            match.Master?.Value.Year ?? release.Year,
            CoverArtUrl: null,
            tracks.Select(t => new CandidateTrack(t.DiscNumber, t.TrackNumber, t.Title, t.DurationMs, null)).ToList());
    }
}
