using System.Globalization;
using System.Text.RegularExpressions;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Library;

namespace MusicHoarder.Api.Catalog.Identity;

/// <summary>
/// Pure Discogs match scoring: title/artist key equality, track count, durations (±3s), year,
/// barcode and catalog number. No IO.
/// </summary>
public static partial class DiscogsMatchScoring
{
    public const int DurationToleranceSeconds = 3;

    public sealed record LibraryTrack(string Title, int? DurationSeconds);

    public sealed record LibraryAlbumHint(
        string AlbumArtist,
        string Album,
        int? Year,
        string? Barcode,
        string? CatalogNumber,
        string? Label,
        IReadOnlyList<LibraryTrack> Tracks);

    public sealed record ScoredRelease(
        long DiscogsId,
        long? MasterId,
        string? Title,
        string? ArtistDisplay,
        double Confidence,
        string EvidenceJson);

    /// <summary>Subject key for a library artist.</summary>
    public static string ArtistSubjectKey(string artistName) => CatalogKeys.NameKey(artistName);

    /// <summary>Subject key for a library album: artist key + U+001F + album key.</summary>
    public static string AlbumSubjectKey(string albumArtist, string album) =>
        CatalogKeys.NameKey(albumArtist) + '\u001f' + CatalogKeys.TitleKey(album);

    public static bool TitlesEqual(string? a, string? b) =>
        CatalogKeys.TitleKey(a) == CatalogKeys.TitleKey(b) && CatalogKeys.TitleKey(a).Length > 0;

    public static bool ArtistsEqual(string? a, string? b) =>
        CatalogKeys.NameKey(a) == CatalogKeys.NameKey(b) && CatalogKeys.NameKey(a).Length > 0;

    public static bool DurationsMatch(int? aSeconds, int? bSeconds)
    {
        if (aSeconds is null or <= 0 || bSeconds is null or <= 0)
            return false;
        return Math.Abs(aSeconds.Value - bSeconds.Value) <= DurationToleranceSeconds;
    }

    public static int? ParseDurationSeconds(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
            return null;

        var text = duration.Trim();
        var parts = text.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 3)
            return null;

        var values = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]) || values[i] < 0)
                return null;
        }

        return parts.Length switch
        {
            1 => values[0],
            2 => values[0] * 60 + values[1],
            _ => values[0] * 3600 + values[1] * 60 + values[2],
        };
    }

    /// <summary>
    /// Confidence 0–1 for a Discogs release against a library album. Exact title+artist keys are
    /// required for a non-zero score; barcode/catno agreement, track count and durations raise it.
    /// </summary>
    public static double ScoreRelease(LibraryAlbumHint library, DiscogsRelease release)
    {
        var releaseArtist = FormatReleaseArtist(release.Artists);
        if (!ArtistsEqual(library.AlbumArtist, releaseArtist) || !TitlesEqual(library.Album, release.Title))
            return 0;

        var score = 0.55; // base for exact artist+title keys

        if (library.Year is int year && release.Year is int ry && year == ry)
            score += 0.05;

        if (CatalogKeys.IsUsableBarcode(library.Barcode)
            && release.Identifiers is { Count: > 0 }
            && release.Identifiers.Any(id =>
                IsBarcodeType(id.Type)
                && CatalogKeys.IsUsableBarcode(id.Value)
                && NormalizeBarcode(id.Value) == NormalizeBarcode(library.Barcode)))
        {
            score += 0.25;
        }

        if (!string.IsNullOrWhiteSpace(library.CatalogNumber)
            && release.Labels is { Count: > 0 }
            && release.Labels.Any(l =>
                !string.IsNullOrWhiteSpace(l.Catno)
                && CatalogKeys.NameKey(l.Catno) == CatalogKeys.NameKey(library.CatalogNumber)
                && (string.IsNullOrWhiteSpace(library.Label)
                    || CatalogKeys.NameKey(l.Name) == CatalogKeys.NameKey(library.Label))))
        {
            score += 0.15;
        }

        var discogsTracks = FlattenTracks(release.Tracklist).ToList();
        var libraryTracks = library.Tracks;
        if (libraryTracks.Count > 0 && discogsTracks.Count > 0)
        {
            if (libraryTracks.Count == discogsTracks.Count)
                score += 0.08;

            var titleHits = libraryTracks.Count(lt =>
                discogsTracks.Any(dt => TitlesEqual(lt.Title, dt.Title)));
            var titleRatio = (double)titleHits / libraryTracks.Count;
            score += 0.12 * titleRatio;

            var withDuration = libraryTracks.Where(t => t.DurationSeconds is > 0).ToList();
            if (withDuration.Count > 0)
            {
                var durationHits = withDuration.Count(lt =>
                    discogsTracks.Any(dt =>
                        TitlesEqual(lt.Title, dt.Title)
                        && DurationsMatch(lt.DurationSeconds, dt.DurationSeconds)));
                score += 0.10 * ((double)durationHits / withDuration.Count);
            }
        }

        return Math.Clamp(score, 0, 1);
    }

    /// <summary>Score a search hit when the full release is not yet loaded (weaker evidence).</summary>
    public static double ScoreSearchHit(LibraryAlbumHint library, DiscogsSearchResult hit)
    {
        var (_, albumPart) = SplitSearchTitle(hit.Title);
        if (!TitlesEqual(library.Album, albumPart))
            return 0;

        var score = 0.40;
        if (library.Year is int year && hit.Year == year)
            score += 0.05;

        if (CatalogKeys.IsUsableBarcode(library.Barcode)
            && hit.Barcode is { Count: > 0 }
            && hit.Barcode.Any(b => CatalogKeys.IsUsableBarcode(b) && NormalizeBarcode(b) == NormalizeBarcode(library.Barcode)))
        {
            score += 0.35;
        }

        if (!string.IsNullOrWhiteSpace(library.CatalogNumber)
            && !string.IsNullOrWhiteSpace(hit.Catno)
            && CatalogKeys.NameKey(hit.Catno) == CatalogKeys.NameKey(library.CatalogNumber))
        {
            score += 0.20;
        }

        return Math.Clamp(score, 0, 1);
    }

    /// <summary>
    /// Splits a Discogs search title, usually "Artist - Album", into its parts. Artist is null when
    /// the title has no separator.
    /// </summary>
    public static (string? Artist, string Album) SplitSearchTitle(string? title)
    {
        var text = title ?? string.Empty;
        var dash = text.LastIndexOf(" - ", StringComparison.Ordinal);
        return dash < 0 ? (null, text) : (text[..dash], text[(dash + 3)..]);
    }

    /// <summary>
    /// Whether the artist part of a search hit's title is the library album artist. Discogs credits
    /// compilations to "Various", which matches any various-artists sentinel on the library side.
    /// A title without an artist part never matches.
    /// </summary>
    public static bool SearchHitArtistMatches(LibraryAlbumHint library, DiscogsSearchResult hit)
    {
        var (artist, _) = SplitSearchTitle(hit.Title);
        if (string.IsNullOrWhiteSpace(artist))
            return false;
        if (ArtistsEqual(library.AlbumArtist, artist))
            return true;
        return DestinationPathResolver.IsVariousArtistsSentinel(library.AlbumArtist)
            && DestinationPathResolver.IsVariousArtistsSentinel(CatalogKeys.StripDisambiguator(artist));
    }

    public static string FormatReleaseArtist(IReadOnlyList<DiscogsArtistRef>? artists)
    {
        if (artists is null or { Count: 0 })
            return string.Empty;

        var parts = new List<string>(artists.Count);
        foreach (var a in artists)
        {
            var name = CatalogKeys.StripDisambiguator(a.Name);
            var join = (a.Join ?? string.Empty).Trim();
            parts.Add(join == "," ? name + "," : string.IsNullOrEmpty(join) ? name : name + " " + join);
        }

        return string.Join(" ", parts).Replace(" ,", ",", StringComparison.Ordinal).Trim();
    }

    public static IEnumerable<(string Title, int? DurationSeconds)> FlattenTracks(IReadOnlyList<DiscogsTrack>? tracklist)
    {
        if (tracklist is null)
            yield break;

        foreach (var track in tracklist)
        {
            var type = track.Type ?? "track";
            if (string.Equals(type, "track", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(track.Title))
            {
                yield return (track.Title!, ParseDurationSeconds(track.Duration));
            }

            if (track.SubTracks is { Count: > 0 })
            {
                foreach (var sub in FlattenTracks(track.SubTracks))
                    yield return sub;
            }
        }
    }

    public static string NormalizeBarcode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return DigitsOnly().Replace(value.Trim(), "");
    }

    private static bool IsBarcodeType(string? type) =>
        type is not null
        && (type.Contains("Barcode", StringComparison.OrdinalIgnoreCase)
            || type.Equals("UPC", StringComparison.OrdinalIgnoreCase)
            || type.Equals("EAN", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"\D+", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsOnly();
}
