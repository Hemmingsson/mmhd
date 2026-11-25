using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>What an album's owned songs say about it, for finding its Discogs release.</summary>
public sealed record DiscogsAlbumHints(
    string AlbumArtist,
    string Album,
    long? DiscogsReleaseId = null,
    string? Upc = null,
    int? Year = null,
    int? TrackCount = null);

/// <param name="EditionExact">The release is the file's own edition (known id or barcode), so its
/// label, catalog number and barcode describe the file. A name match only knows the album (master).</param>
public sealed record DiscogsAlbumMatch(
    DiscogsDocument<DiscogsRelease> Release,
    DiscogsDocument<DiscogsMaster>? Master,
    double Confidence,
    string Method,
    bool EditionExact);

/// <summary>
/// Finds an album's Discogs release: by a known release id, then by barcode, then by a conservative
/// artist + release-title search where both keys must be equal and the tracklist must not contradict.
/// A name match resolves to the master's main release. Answers are cached per album (misses too), so an
/// album costs at most three requests (search, master, release) however many of its songs ask.
/// </summary>
public sealed partial class DiscogsReleaseMatcher(
    IDiscogsClient client,
    IMemoryCache cache,
    IOptionsMonitor<DiscogsOptions> options,
    ILogger<DiscogsReleaseMatcher> logger)
{
    /// <summary>Discogs' "Various" artist, which says nothing about whose album it is.</summary>
    public const long VariousArtistsId = 194;

    private sealed record CacheEntry(DiscogsAlbumMatch? Match);

    public async Task<DiscogsAlbumMatch?> MatchAsync(DiscogsAlbumHints hints, CancellationToken ct = default)
    {
        var key = CacheKey(hints);
        if (cache.TryGetValue(key, out CacheEntry? hit) && hit is not null)
            return hit.Match;

        var match = await FindAsync(hints, ct);
        cache.Set(key, new CacheEntry(match), TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.CacheMinutes)));
        return match;
    }

    private async Task<DiscogsAlbumMatch?> FindAsync(DiscogsAlbumHints hints, CancellationToken ct)
    {
        if (hints.DiscogsReleaseId is { } releaseId
            && await client.GetReleaseAsync(releaseId, ct) is { } known)
        {
            return new DiscogsAlbumMatch(known, await MasterOfAsync(known.Value, ct), 1.0, "release-id", EditionExact: true);
        }

        // Search needs a token; without one only a known release id can be read.
        if (!client.IsAuthenticated)
            return null;

        var albumKey = TitleNormalizer.NormalizeForSearch(hints.Album);
        var artistKey = ArtistKey(hints.AlbumArtist);
        if (albumKey.Length == 0 || artistKey.Length == 0)
            return null;

        if (UsableBarcode(hints.Upc) is { } barcode)
        {
            var byBarcode = await client.SearchAsync(new DiscogsSearchQuery(Type: "release", Barcode: barcode), perPage: 10, ct: ct);
            var hit = byBarcode.Items.FirstOrDefault(r => TitleKeys(r) is var (a, t) && t == albumKey && (a.Length == 0 || a == artistKey));
            if (hit is not null && await client.GetReleaseAsync(hit.Id, ct) is { } edition)
                return Scored(new DiscogsAlbumMatch(edition, await MasterOfAsync(edition.Value, ct), 0.95, "barcode", EditionExact: true), hints);
        }

        var results = await client.SearchAsync(
            new DiscogsSearchQuery(Type: "release", Artist: hints.AlbumArtist, ReleaseTitle: hints.Album), perPage: 25, ct: ct);
        var agreeing = results.Items
            .Where(r => TitleKeys(r) is var (a, t) && a == artistKey && t == albumKey)
            .ToList();
        if (agreeing.Count == 0)
        {
            logger.LogDebug("Discogs: no release named {Album} by {Artist}", hints.Album, hints.AlbumArtist);
            return null;
        }

        // Editions group under their master; the album is the master most of the results belong to.
        var group = agreeing
            .GroupBy(r => r.MasterId ?? -r.Id)
            .OrderByDescending(g => g.Count())
            .First();
        var masterId = group.First().MasterId;

        DiscogsDocument<DiscogsMaster>? master = null;
        long pickedId = group.First().Id;
        if (masterId is { } mid && await client.GetMasterAsync(mid, ct) is { } masterDoc)
        {
            master = masterDoc;
            pickedId = masterDoc.Value.MainRelease ?? pickedId;
        }

        if (await client.GetReleaseAsync(pickedId, ct) is not { } release)
            return null;

        // A master-less release found by name is still only one listing among the search results.
        var exact = masterId is null && agreeing.Count == 1;
        return Scored(new DiscogsAlbumMatch(release, master, 0.85, "search", exact), hints);
    }

    /// <summary>Adjusts a match by how well its tracklist and year fit the owned songs.</summary>
    private static DiscogsAlbumMatch Scored(DiscogsAlbumMatch match, DiscogsAlbumHints hints)
    {
        var confidence = match.Confidence;
        var year = match.Master?.Value.Year ?? match.Release.Value.Year;
        if (hints.Year is { } y && year is { } dy)
            confidence += Math.Abs(y - dy) <= 1 ? 0.05 : -0.05;

        if (hints.TrackCount is { } owned && owned > 0)
        {
            var listed = DiscogsTracklist.Flatten(match.Release.Value.Tracklist).Count;
            var delta = Math.Abs(listed - owned);
            if (listed > 0 && delta <= 2)
                confidence += 0.05;
            else if (listed > 0 && delta > 3 && delta > owned / 4)
                confidence -= 0.25;
        }

        return match with { Confidence = Math.Clamp(confidence, 0, 1) };
    }

    private async Task<DiscogsDocument<DiscogsMaster>?> MasterOfAsync(DiscogsRelease release, CancellationToken ct)
        => release.MasterId is { } id ? await client.GetMasterAsync(id, ct) : null;

    /// <summary>Search results title themselves "Artist - Title"; answers the artist and title keys.</summary>
    internal static (string Artist, string Title) TitleKeys(DiscogsSearchResult result)
    {
        var title = result.Title ?? string.Empty;
        var split = title.IndexOf(" - ", StringComparison.Ordinal);
        return split < 0
            ? (string.Empty, TitleNormalizer.NormalizeForSearch(title))
            : (ArtistKey(title[..split]), TitleNormalizer.NormalizeForSearch(title[(split + 3)..]));
    }

    /// <summary>Artist key without Discogs' "(2)" disambiguator and "*" name-variation marker.</summary>
    public static string ArtistKey(string? name) => TitleNormalizer.NormalizeForSearch(DisplayName(name));

    /// <summary>A Discogs artist or label name as shown: no "(2)" suffix, no trailing "*".</summary>
    public static string? DisplayName(string? name)
        => string.IsNullOrWhiteSpace(name) ? name : Disambiguator().Replace(name.Trim(), string.Empty).TrimEnd('*').Trim();

    /// <summary>
    /// Selects the release artist that represents the library album artist. A compilation has no
    /// single performer to promote to album artist, even when its release credits list performers.
    /// </summary>
    public static DiscogsArtistRef? AlbumArtist(
        IReadOnlyList<DiscogsArtistRef>? artists, string? libraryAlbumArtist)
    {
        if (artists is null or { Count: 0 } || artists.Any(a => a.Id == VariousArtistsId))
            return null;

        var key = ArtistKey(libraryAlbumArtist);
        return artists.FirstOrDefault(a => ArtistKey(a.Name) == key)
            ?? artists.FirstOrDefault(a => a.Id != VariousArtistsId);
    }

    /// <summary>The digits of a barcode worth searching for; Discogs and tags both carry "none" and junk.</summary>
    public static string? UsableBarcode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        return digits.Length is >= 8 and <= 14 ? digits : null;
    }

    private static string CacheKey(DiscogsAlbumHints hints)
        => string.Create(CultureInfo.InvariantCulture,
            $"discogs:album:{hints.DiscogsReleaseId}|{UsableBarcode(hints.Upc)}|{ArtistKey(hints.AlbumArtist)}|{TitleNormalizer.NormalizeForSearch(hints.Album)}");

    [GeneratedRegex(@"\s*\(\d+\)$")]
    private static partial Regex Disambiguator();
}

/// <summary>A Discogs tracklist as playable tracks: headings dropped, an index track counted once.</summary>
public static partial class DiscogsTracklist
{
    public sealed record Track(int DiscNumber, int TrackNumber, string Title, int? DurationMs, string? Position);

    public static IReadOnlyList<Track> Flatten(IReadOnlyList<DiscogsTrack>? tracklist)
    {
        var tracks = new List<Track>();
        if (tracklist is null)
            return tracks;

        var perDisc = new Dictionary<int, int>();
        foreach (var t in tracklist)
        {
            if (string.Equals(t.Type, "heading", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(t.Title))
                continue;

            var duration = DurationMs(t.Duration);
            if (duration is null && t.SubTracks is { Count: > 0 } subs)
            {
                var parts = subs.Select(s => DurationMs(s.Duration)).ToList();
                duration = parts.All(p => p is not null) ? parts.Sum() : null;
            }

            var (disc, number) = Position(t.Position);
            var next = perDisc.GetValueOrDefault(disc) + 1;
            perDisc[disc] = number ?? next;
            tracks.Add(new Track(disc, number ?? next, t.Title!.Trim(), duration, t.Position));
        }

        return tracks;
    }

    /// <summary>"m:ss" or "h:mm:ss" in milliseconds; null when blank or malformed.</summary>
    public static int? DurationMs(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
            return null;
        var parts = duration.Trim().Split(':');
        if (parts.Length is < 2 or > 3 || !parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            return null;
        var seconds = parts.Aggregate(0, (acc, p) => acc * 60 + int.Parse(p, CultureInfo.InvariantCulture));
        return seconds * 1000;
    }

    /// <summary>
    /// Disc and track number from a position: "2-3" / "CD2-3" is disc 2 track 3, "7" is track 7,
    /// vinyl sides ("A1", "B2") are one disc numbered in order (null track: the caller counts).
    /// </summary>
    internal static (int Disc, int? Track) Position(string? position)
    {
        if (string.IsNullOrWhiteSpace(position))
            return (1, null);
        var p = position.Trim();
        if (DiscTrack().Match(p) is { Success: true } m)
            return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        if (int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return (1, n);
        return (1, null);
    }

    [GeneratedRegex(@"^(?:CD|DVD|Disc\s*)?(\d+)[-.](\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscTrack();
}
