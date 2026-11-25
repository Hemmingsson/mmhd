using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.AlbumTracklist;

/// <summary>
/// Reconciles several providers' album tracklists into one canonical list, mirroring the tag-metadata
/// consensus model (<see cref="ReleaseSelector"/> / <c>ConsensusEvaluator</c>): cluster the candidates
/// whose albums agree (so editions of the same album group together), let the cluster the most distinct
/// providers vote for win, then build the canonical tracks by per-slot voting — recording which
/// providers corroborate each track and flagging tracks/lengths the providers disagree on. Candidates
/// in a losing cluster (a genuinely different edition) are kept as alternate <see cref="ReconciledSource"/>s
/// for display, not merged.
/// </summary>
public static class AlbumTracklistReconciler
{
    /// <summary>Fuzzy ratio (0–100) above which two album/track titles are treated as the same.</summary>
    public const double DefaultIdentityThreshold = 85.0;

    public sealed record ReconciledTracklist(
        string? Title,
        string? AlbumArtist,
        int? Year,
        string? CoverArtUrl,
        int ResolvedTrackCount,
        bool TrackCountContested,
        IReadOnlyList<ReconciledTrack> Tracks,
        IReadOnlyList<ReconciledSource> Sources);

    public sealed record ReconciledTrack(
        int DiscNumber,
        int TrackNumber,
        string? Title,
        int? DurationMs,
        string? MusicBrainzRecordingId,
        IReadOnlyList<EnrichmentProvider> CorroboratingProviders,
        bool IsContested);

    public sealed record ReconciledSource(
        EnrichmentProvider Provider,
        string? AlbumId,
        int TrackCount,
        bool InWinningCluster);

    public static ReconciledTracklist? Reconcile(
        IReadOnlyList<AlbumTracklistCandidate> candidates,
        double identityThreshold = DefaultIdentityThreshold)
    {
        var usable = candidates.Where(c => c.Tracks.Count > 0).ToList();

        // A supporting provider (Discogs) never votes: it joins no cluster, adds no slot and does not
        // count toward a track being contested. It only corroborates slots the voting providers list.
        var voting = usable.Where(c => !FieldAuthority.IsSupporting(c.Source)).ToList();
        if (voting.Count == 0)
            return null;

        // Cluster candidates whose album titles agree (editions of the same album → one cluster).
        var clusters = new List<List<AlbumTracklistCandidate>>();
        foreach (var c in voting)
        {
            var placed = false;
            foreach (var g in clusters)
            {
                if (SameAlbum(g[0], c, identityThreshold))
                {
                    g.Add(c);
                    placed = true;
                    break;
                }
            }

            if (!placed)
                clusters.Add([c]);
        }

        // Winning cluster: most distinct providers, then MusicBrainz present (canonical DB), then the
        // earliest/original pressing, then the larger tracklist as a final tiebreak.
        var winning = clusters
            .OrderByDescending(g => g.Select(c => c.Source).Distinct().Count())
            .ThenByDescending(g => g.Any(c => c.Source == EnrichmentProvider.MusicBrainzWeb) ? 1 : 0)
            .ThenBy(g => EarliestYear(g) ?? int.MaxValue)
            .ThenByDescending(g => g.Max(c => c.Tracks.Count))
            .First();

        var clusterProviders = winning.Select(c => c.Source).Distinct().Count();
        var supporting = usable
            .Where(c => FieldAuthority.IsSupporting(c.Source) && SameAlbum(winning[0], c, identityThreshold))
            .ToList();

        // Track-count consensus: most-voted count among the agreeing providers; contested when they differ.
        var counts = winning.Select(c => c.Tracks.Count).ToList();
        var resolvedCount = counts
            .GroupBy(n => n)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key)
            .First().Key;
        var trackCountContested = counts.Distinct().Count() > 1;

        // Build canonical tracks over the union of (disc, position) slots present in the winning cluster.
        var slots = winning
            .SelectMany(c => c.Tracks.Select(t => (t.DiscNumber, t.TrackNumber)))
            .Distinct()
            .OrderBy(s => s.DiscNumber)
            .ThenBy(s => s.TrackNumber)
            .ToList();

        var tracks = new List<ReconciledTrack>(slots.Count);
        foreach (var (disc, pos) in slots)
        {
            var atSlot = winning
                .Select(c => (c.Source, Track: c.Tracks.FirstOrDefault(t => t.DiscNumber == disc && t.TrackNumber == pos)))
                .Where(x => x.Track is not null)
                .Select(x => (x.Source, Track: x.Track!))
                .ToList();

            var title = MostVotedTitle(atSlot);
            var corroborating = atSlot
                .Where(x => FuzzyTextMatch.Ratio(title, x.Track.Title) is not double r || r >= identityThreshold)
                .Select(x => x.Source)
                .Distinct()
                .ToList();
            // Contested when not every provider in the cluster backs this exact track (bonus track
            // only some editions have, or a title disagreement).
            var contested = corroborating.Count < clusterProviders;
            corroborating.AddRange(supporting
                .Where(c => c.Tracks.FirstOrDefault(t => t.DiscNumber == disc && t.TrackNumber == pos) is { } track
                    && (FuzzyTextMatch.Ratio(title, track.Title) is not double r || r >= identityThreshold))
                .Select(c => c.Source)
                .Distinct()
                .Where(s => !corroborating.Contains(s))
                .ToList());

            tracks.Add(new ReconciledTrack(
                DiscNumber: disc,
                TrackNumber: pos,
                Title: title,
                DurationMs: MedianDuration(atSlot.Select(x => x.Track.DurationMs)),
                MusicBrainzRecordingId: atSlot
                    .FirstOrDefault(x => x.Source == EnrichmentProvider.MusicBrainzWeb).Track?.ProviderRecordingId,
                CorroboratingProviders: corroborating,
                IsContested: contested));
        }

        return new ReconciledTracklist(
            Title: MostVotedString(winning.Select(c => c.Title)),
            AlbumArtist: MostVotedString(winning.Select(c => c.AlbumArtist)),
            Year: EarliestYear(winning),
            CoverArtUrl: winning.FirstOrDefault(c => c.Source == EnrichmentProvider.SpotifyAPI && !string.IsNullOrWhiteSpace(c.CoverArtUrl))?.CoverArtUrl
                ?? winning.FirstOrDefault(c => c.Source == EnrichmentProvider.MusicBrainzWeb && !string.IsNullOrWhiteSpace(c.CoverArtUrl))?.CoverArtUrl
                ?? winning.Select(c => c.CoverArtUrl).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)),
            ResolvedTrackCount: resolvedCount,
            TrackCountContested: trackCountContested,
            Tracks: tracks,
            Sources: candidates
                .Where(c => c.Tracks.Count > 0)
                .Select(c => new ReconciledSource(
                    c.Source, c.ProviderAlbumId, c.Tracks.Count, winning.Contains(c) || supporting.Contains(c)))
                .ToList());
    }

    /// <summary>
    /// Album titles agree. With no title on either side, track-count proximity decides, so a title-less
    /// candidate still joins the album it clearly belongs to instead of starting a lone cluster.
    /// </summary>
    private static bool SameAlbum(AlbumTracklistCandidate a, AlbumTracklistCandidate b, double identityThreshold)
        => !string.IsNullOrWhiteSpace(a.Title) && !string.IsNullOrWhiteSpace(b.Title)
            ? FuzzyTextMatch.Ratio(a.Title, b.Title) is double r && r >= identityThreshold
            : Math.Abs(a.Tracks.Count - b.Tracks.Count) <= 1;

    private static int? EarliestYear(List<AlbumTracklistCandidate> cluster)
    {
        var years = cluster.Where(c => c.Year is > 0).Select(c => c.Year!.Value).ToList();
        return years.Count == 0 ? null : years.Min();
    }

    /// <summary>Most-voted track title at a slot (distinct providers, then most complete string).</summary>
    private static string? MostVotedTitle(List<(EnrichmentProvider Source, CandidateTrack Track)> atSlot)
        => MostVotedString(atSlot.Select(x => x.Track.Title));

    /// <summary>Most-voted non-empty string by frequency, falling back to the longest sample.</summary>
    private static string? MostVotedString(IEnumerable<string?> values)
    {
        var present = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
        if (present.Count == 0)
            return null;

        return present
            .GroupBy(v => TitleNormalizer.NormalizeForSearch(v))
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Max(v => v.Length))
            .First()
            .OrderByDescending(v => v.Length)
            .First();
    }

    private static int? MedianDuration(IEnumerable<int?> durations)
    {
        var present = durations.Where(d => d is > 0).Select(d => d!.Value).OrderBy(d => d).ToList();
        if (present.Count == 0)
            return null;
        return present[present.Count / 2];
    }
}
