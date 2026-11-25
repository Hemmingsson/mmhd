using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Applies a consensus-matched identity to a song's row <b>without degrading curated metadata</b>.
/// Per display field:
/// <list type="bullet">
/// <item>empty or low-quality existing value → filled/replaced;</item>
/// <item>existing value equals the incoming one (modulo normalization) → no-op;</item>
/// <item>a <i>good</i> existing value that differs → replaced only when the match is a strong
/// multi-provider consensus at high confidence; otherwise the change is recorded as a
/// <b>proposed</b> (not-applied) change for review.</item>
/// </list>
/// Identifiers (ISRC/MBID/Spotify) are additive and always attached. Every applied or proposed
/// change is returned so the caller can persist it to the undo/review history.
/// </summary>
public static class MetadataMerger
{
    public sealed record FieldChange(string Field, string? OldValue, string? NewValue, bool Applied);

    /// <summary>
    /// Album-level fields whose overwrite of a good embedded value is gated on <i>per-field</i>
    /// cluster corroboration (see <paramref name="corroboratedFields"/>) rather than the blanket
    /// recording-consensus flag. A recording legitimately appears on several releases, so agreeing on
    /// the recording must not by itself license replacing a curated album/year/track.
    /// </summary>
    private static readonly HashSet<string> AlbumLevelFields = new(StringComparer.Ordinal)
    {
        "Album", "Year", "TrackNumber", "DiscNumber", "TotalDiscs", "TotalTracks",
        "ReleaseTypePrimary", "ReleaseTypes",
        // Descriptive album-level fields: a recording appearing on a release doesn't corroborate its
        // label/catalog/barcode, so these follow the same per-field corroboration gate.
        "Label", "CatalogNumber", "Upc", "Copyright", "AlbumArtistSort", "ReleaseDate", "OriginalReleaseDate",
    };

    /// <summary>
    /// The album identity the split healer elects per album folder. Once it has corrected a row
    /// (<see cref="SongMetadata.AlbumIdentityHealedAtUtc"/>) these belong to the album: a match fills
    /// them when empty and otherwise leaves them, or re-applying it would undo the heal and requeue
    /// the build, and the next heal would correct it back.
    /// </summary>
    private static readonly HashSet<string> HealedAlbumFields = new(StringComparer.Ordinal)
    {
        "Album", "AlbumArtist", "Year", "TotalDiscs", "ReleaseTypePrimary", "ReleaseTypes",
        "MusicBrainzReleaseId", "MusicBrainzReleaseGroupId", "AlbumArtistMusicBrainzId",
    };

    public static IReadOnlyList<FieldChange> ApplyMatch(
        SongMetadata song,
        EnrichmentProviderResult winner,
        double confidence,
        int agreeingProviderCount,
        double autoUpgradeConfidence,
        string? warningsJson,
        IReadOnlySet<string>? corroboratedFields = null,
        IReadOnlySet<string>? supportingFields = null)
    {
        song.CaptureOriginalMetadata();

        var highConsensus = agreeingProviderCount >= 2 && confidence >= autoUpgradeConfidence;
        var changes = new List<FieldChange>();

        // A supporting provider's release fields fill gaps but never replace curated values.
        bool Supporting(string field) => supportingFields?.Contains(field) == true;

        // Recording/identity fields: the agreeing cluster already corroborated these, so the blanket
        // consensus flag governs whether they may upgrade a good curated value.
        // Album-level fields: only upgrade a good curated value when *that field* was corroborated by
        // ≥2 providers (or no per-field signal exists, e.g. a solo match → fall back to highConsensus).
        bool Upgrade(string field) =>
            !Supporting(field)
            && (corroboratedFields is not null && AlbumLevelFields.Contains(field)
                ? corroboratedFields.Contains(field)
                : highConsensus);

        var healed = song.AlbumIdentityHealedAtUtc is not null;
        bool Healed(string field, object? existing) =>
            healed && HealedAlbumFields.Contains(field)
            && existing switch { null => false, string text => !string.IsNullOrWhiteSpace(text), _ => true };

        MergeText(song, "Artist", song.Artist, winner.Artist, v => song.Artist = v, highConsensus, changes);
        if (!Healed("AlbumArtist", song.AlbumArtist))
            MergeText(song, "AlbumArtist", song.AlbumArtist, winner.AlbumArtist, v => song.AlbumArtist = v, highConsensus, changes);
        MergeText(song, "Title", song.Title, winner.Title, v => song.Title = v, highConsensus, changes);
        if (!Healed("Album", song.Album))
            MergeText(song, "Album", song.Album, winner.Album, v => song.Album = v, Upgrade("Album"), changes);
        MergeText(song, "Artists", song.Artists, winner.Artists, v => song.Artists = v, highConsensus, changes);
        if (!Healed("ReleaseTypePrimary", song.ReleaseTypePrimary))
            MergeText(song, "ReleaseTypePrimary", song.ReleaseTypePrimary, winner.ReleaseTypePrimary, v => song.ReleaseTypePrimary = v, Upgrade("ReleaseTypePrimary"), changes);
        if (!Healed("ReleaseTypes", song.ReleaseTypes))
            MergeText(song, "ReleaseTypes", song.ReleaseTypes, winner.ReleaseTypes, v => song.ReleaseTypes = v, Upgrade("ReleaseTypes"), changes);

        // Descriptive metadata. These are enrichment-sourced (the row starts null), so the initial
        // fill always applies; the gating below only decides rare re-enrichment conflicts. Album-level
        // descriptive fields (label/catalog/upc/copyright/album-artist-sort) are gated on the same
        // per-field corroboration as the other album-level fields. Genres accumulate canonical
        // terms; original provider vocabulary remains independently available in Tags.
        song.Tags = ProviderTags.Combine(song.Tags.Concat(ProviderTags.LegacyTerms(song.Genre)).Concat(ProviderTags.FromResult(winner.Genre, winner.Tags, winner.MatchedBy)));
        var genre = DiscogsGenres.Normalize(MultiValue.Join(new[] { song.Genre, winner.Genre }.Concat(winner.Tags ?? [])));
        if (genre != song.Genre)
        {
            changes.Add(new FieldChange("Genre", song.Genre, genre, Applied: true));
            song.Genre = genre;
        }
        MergeText(song, "Composer", song.Composer, winner.Composer, v => song.Composer = v, highConsensus, changes);
        MergeText(song, "ArtistSort", song.ArtistSort, winner.ArtistSort, v => song.ArtistSort = v, highConsensus, changes);
        MergeText(song, "Label", song.Label, winner.Label, v => song.Label = v, Upgrade("Label"), changes);
        MergeText(song, "CatalogNumber", song.CatalogNumber, winner.CatalogNumber, v => song.CatalogNumber = v, Upgrade("CatalogNumber"), changes);
        MergeText(song, "Upc", song.Upc, winner.Upc, v => song.Upc = v, Upgrade("Upc"), changes);
        MergeText(song, "Copyright", song.Copyright, winner.Copyright, v => song.Copyright = v, Upgrade("Copyright"), changes);
        MergeText(song, "AlbumArtistSort", song.AlbumArtistSort, winner.AlbumArtistSort, v => song.AlbumArtistSort = v, Upgrade("AlbumArtistSort"), changes);
        // Release dates prefer the more *precise* ISO value (a full YYYY-MM-DD beats a bare year) even
        // over an existing good value — SpotiFLAC's "keep the longest candidate" rule.
        MergeReleaseDate(song, "ReleaseDate", song.ReleaseDate, winner.ReleaseDate, v => song.ReleaseDate = v, Upgrade("ReleaseDate"), changes);
        MergeReleaseDate(song, "OriginalReleaseDate", song.OriginalReleaseDate, winner.OriginalReleaseDate, v => song.OriginalReleaseDate = v, Upgrade("OriginalReleaseDate"), changes);
        if (!Healed("Year", song.Year))
            MergeNumber(song, "Year", song.Year, winner.Year, v => song.Year = v, Upgrade("Year"), changes);
        MergeNumber(song, "TrackNumber", song.TrackNumber, winner.TrackNumber, v => song.TrackNumber = v, Upgrade("TrackNumber"), changes);
        MergeNumber(song, "DiscNumber", song.DiscNumber, winner.DiscNumber, v => song.DiscNumber = v, Upgrade("DiscNumber"), changes);
        if (!Healed("TotalDiscs", song.TotalDiscs))
            MergeNumber(song, "TotalDiscs", song.TotalDiscs, winner.TotalDiscs, v => song.TotalDiscs = v, Upgrade("TotalDiscs"), changes);
        MergeNumber(song, "TotalTracks", song.TotalTracks, winner.TotalTracks, v => song.TotalTracks = v, Upgrade("TotalTracks"), changes);

        // Identifiers and the compilation flag are additive facts — attach the matched identity's data.
        if (!string.IsNullOrWhiteSpace(winner.MusicBrainzId)) song.MusicBrainzId = winner.MusicBrainzId;
        if (!string.IsNullOrWhiteSpace(winner.MusicBrainzReleaseId) && !Healed("MusicBrainzReleaseId", song.MusicBrainzReleaseId))
            song.MusicBrainzReleaseId = winner.MusicBrainzReleaseId;
        if (!string.IsNullOrWhiteSpace(winner.MusicBrainzReleaseGroupId) && !Healed("MusicBrainzReleaseGroupId", song.MusicBrainzReleaseGroupId))
            song.MusicBrainzReleaseGroupId = winner.MusicBrainzReleaseGroupId;
        if (!string.IsNullOrWhiteSpace(winner.AlbumArtistMusicBrainzId) && !Healed("AlbumArtistMusicBrainzId", song.AlbumArtistMusicBrainzId))
            song.AlbumArtistMusicBrainzId = winner.AlbumArtistMusicBrainzId;
        if (!string.IsNullOrWhiteSpace(winner.ArtistMusicBrainzIds)) song.ArtistMusicBrainzIds = winner.ArtistMusicBrainzIds;
        if (winner.IsCompilation is true) song.IsCompilation = true;
        if (!string.IsNullOrWhiteSpace(winner.SpotifyId)) song.SpotifyId = winner.SpotifyId;
        if (!string.IsNullOrWhiteSpace(winner.AcoustIdTrackId)) song.AcoustIdTrackId = winner.AcoustIdTrackId;
        if (!string.IsNullOrWhiteSpace(winner.Isrc)) song.Isrc = winner.Isrc;

        // Discogs ids only fill: a release a person chose or the backfill linked is never re-pointed
        // by a later match. Recorded so the history shows where the link came from.
        FillId("DiscogsReleaseId", song.DiscogsReleaseId, winner.DiscogsReleaseId, v => song.DiscogsReleaseId = v, changes);
        FillId("DiscogsMasterId", song.DiscogsMasterId, winner.DiscogsMasterId, v => song.DiscogsMasterId = v, changes);
        FillId("DiscogsArtistId", song.DiscogsArtistId, winner.DiscogsArtistId, v => song.DiscogsArtistId = v, changes);

        song.MatchedBy = winner.MatchedBy;
        song.MatchConfidence = confidence;
        song.MatchWarnings = warningsJson;
        song.EnrichmentStatus = EnrichmentStatus.Matched;
        song.EnrichedAtUtc = DateTime.UtcNow;
        song.EnrichmentError = null;

        return changes;
    }

    /// <summary>True for a change that came from the supporting provider rather than the winner.</summary>
    public static bool IsSupportingChange(string field, IReadOnlySet<string>? supportingFields)
        => supportingFields?.Contains(field) == true || field.StartsWith("Discogs", StringComparison.Ordinal);

    private static void FillId(string field, long? existing, long? incoming, Action<long?> set, List<FieldChange> changes)
    {
        if (incoming is null || existing is not null)
            return;
        set(incoming);
        changes.Add(new FieldChange(field, null, incoming.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), Applied: true));
    }

    private static void MergeText(
        SongMetadata song, string field, string? existing, string? incoming,
        Action<string?> set, bool mayUpgrade, List<FieldChange> changes)
    {
        if (string.IsNullOrWhiteSpace(incoming))
            return;

        if (string.Equals(existing, incoming, StringComparison.Ordinal))
            return;

        if (string.IsNullOrWhiteSpace(existing) || MetadataQualityHeuristics.IsLowQuality(existing, song.FileName))
        {
            set(incoming);
            changes.Add(new FieldChange(field, existing, incoming, Applied: true));
            return;
        }

        if (string.Equals(
                TitleNormalizer.NormalizeForSearch(existing),
                TitleNormalizer.NormalizeForSearch(incoming),
                StringComparison.Ordinal))
            return; // same value, just spelled differently — keep the curated form

        if (mayUpgrade)
        {
            set(incoming);
            changes.Add(new FieldChange(field, existing, incoming, Applied: true));
        }
        else
        {
            changes.Add(new FieldChange(field, existing, incoming, Applied: false)); // proposed
        }
    }

    /// <summary>
    /// Merges an ISO release-date string, preferring the more <i>precise</i> value: a full
    /// <c>YYYY-MM-DD</c> supersedes a bare <c>YYYY</c> even when the existing value is otherwise good,
    /// because it's strictly more information about the same release (SpotiFLAC's "keep the longest
    /// candidate" rule). A less-precise or equal-precision incoming value is a no-op. When the two
    /// disagree on the leading year, the normal <paramref name="mayUpgrade"/> gate applies.
    /// </summary>
    private static void MergeReleaseDate(
        SongMetadata song, string field, string? existing, string? incoming,
        Action<string?> set, bool mayUpgrade, List<FieldChange> changes)
    {
        if (string.IsNullOrWhiteSpace(incoming))
            return;

        if (string.IsNullOrWhiteSpace(existing))
        {
            set(incoming);
            changes.Add(new FieldChange(field, existing, incoming, Applied: true));
            return;
        }

        if (string.Equals(existing, incoming, StringComparison.Ordinal))
            return;

        // Same release (leading year agrees) but incoming carries more precision → always upgrade.
        var sameYear = ReleaseDateParser.ParseYear(existing) is int y && ReleaseDateParser.ParseYear(incoming) == y;
        if (sameYear && incoming.Trim().Length > existing.Trim().Length)
        {
            set(incoming);
            changes.Add(new FieldChange(field, existing, incoming, Applied: true));
            return;
        }

        if (sameYear)
            return; // equal or lower precision on the same year — keep the curated value

        // Genuinely different year: defer to the standard upgrade gate.
        if (mayUpgrade)
        {
            set(incoming);
            changes.Add(new FieldChange(field, existing, incoming, Applied: true));
        }
        else
        {
            changes.Add(new FieldChange(field, existing, incoming, Applied: false));
        }
    }

    private static void MergeNumber(
        SongMetadata song, string field, int? existing, int? incoming,
        Action<int?> set, bool mayUpgrade, List<FieldChange> changes)
    {
        if (incoming is null)
            return;

        if (existing is null)
        {
            set(incoming);
            changes.Add(new FieldChange(field, null, incoming.ToString(), Applied: true));
            return;
        }

        if (existing == incoming)
            return;

        if (mayUpgrade)
        {
            set(incoming);
            changes.Add(new FieldChange(field, existing.ToString(), incoming.ToString(), Applied: true));
        }
        else
        {
            changes.Add(new FieldChange(field, existing.ToString(), incoming.ToString(), Applied: false));
        }
    }
}
