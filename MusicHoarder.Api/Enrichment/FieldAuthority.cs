using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Which source owns which song field once consensus has picked a recording. The voting providers
/// (AcoustID, MusicBrainz, Spotify, Deezer, Apple Music, the trackers) decide the identity and every
/// field they always did. A <i>supporting</i> provider (Discogs) never votes: its candidate is set
/// aside, and when its release names the same album as the winner it overlays the catalog fields it is
/// the authority on. It is never a dissenter, never a quorum member and never something to wait on.
/// </summary>
public static class FieldAuthority
{
    /// <summary>
    /// The Discogs fields the overlay may write. The merger fills them when empty but never replaces a
    /// good existing value with them; that difference is proposed for review instead.
    /// </summary>
    public static readonly IReadOnlySet<string> DiscogsFields = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(SongMetadata.Genre),
        nameof(SongMetadata.Label),
        nameof(SongMetadata.CatalogNumber),
        nameof(SongMetadata.Upc),
    };

    public static bool IsSupporting(EnrichmentProvider provider) => provider is EnrichmentProvider.Discogs;

    public static bool IsBroadGenre(string value) => Metadata.DiscogsGenres.IsBroadGenre(value);

    /// <summary>
    /// The winner with the Discogs catalog fields laid over it, and the fields that came from Discogs.
    /// Returns the winner unchanged (and no fields) when the Discogs release is a different album.
    /// Label and catalog number replace the winner's (Discogs is the label authority), the barcode only
    /// fills a gap (a streaming UPC names the digital edition the file is), styles replace the genre.
    /// </summary>
    public static (EnrichmentProviderResult Winner, IReadOnlySet<string> Fields) OverlayDiscogs(
        EnrichmentProviderResult winner, EnrichmentProviderResult discogs)
    {
        if (!SameAlbum(winner, discogs))
            return (winner, new HashSet<string>());

        var fields = new HashSet<string>(StringComparer.Ordinal);
        var label = Pick(winner.Label, discogs.Label, replace: true, nameof(SongMetadata.Label), fields);
        var catalogNumber = Pick(winner.CatalogNumber, discogs.CatalogNumber, replace: true, nameof(SongMetadata.CatalogNumber), fields);
        var upc = Pick(winner.Upc, discogs.Upc, replace: false, nameof(SongMetadata.Upc), fields);
        var genre = Pick(winner.Genre, StylesOnly(discogs.Genre), replace: true, nameof(SongMetadata.Genre), fields);

        return (winner with
        {
            Label = label,
            CatalogNumber = catalogNumber,
            Upc = upc,
            Genre = genre,
            DiscogsReleaseId = discogs.DiscogsReleaseId ?? winner.DiscogsReleaseId,
            DiscogsMasterId = discogs.DiscogsMasterId ?? winner.DiscogsMasterId,
            DiscogsArtistId = discogs.DiscogsArtistId ?? winner.DiscogsArtistId,
        }, fields);
    }

    /// <summary>
    /// True when the existing genre is the incoming styles plus only Discogs broad genres, so swapping
    /// it for the styles drops nothing a person chose.
    /// </summary>
    public static bool IsStylesOfStoredGenre(string? existing, string? incomingStyles)
    {
        var styles = Metadata.MultiValue.Split(incomingStyles);
        if (styles.Length == 0)
            return false;

        var stored = Metadata.MultiValue.Split(existing);
        var remainder = stored.Where(s => !styles.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        return remainder.Count < stored.Length
            && remainder.All(IsBroadGenre)
            && styles.All(s => stored.Contains(s, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The same album: equal album titles and the Discogs artist is the winner's album artist
    /// (or one of its credited artists), compared on the search keys (which drop "(2)" and "*").</summary>
    internal static bool SameAlbum(EnrichmentProviderResult winner, EnrichmentProviderResult discogs)
    {
        var album = TitleNormalizer.NormalizeForSearch(winner.Album);
        if (album.Length == 0 || album != TitleNormalizer.NormalizeForSearch(discogs.Album))
            return false;

        var discogsArtist = TitleNormalizer.NormalizeForSearch(discogs.AlbumArtist ?? discogs.Artist);
        if (discogsArtist.Length == 0)
            return false;

        return new[] { winner.AlbumArtist, winner.Artist }
            .Concat(Metadata.MultiValue.Split(winner.Artists))
            .Any(a => TitleNormalizer.NormalizeForSearch(a) == discogsArtist);
    }

    private static string? StylesOnly(string? genre)
        => Metadata.MultiValue.Join(Metadata.MultiValue.Split(genre).Where(g => !IsBroadGenre(g)));

    private static string? Pick(string? current, string? incoming, bool replace, string field, HashSet<string> fields)
    {
        if (string.IsNullOrWhiteSpace(incoming))
            return current;
        if (!replace && !string.IsNullOrWhiteSpace(current))
            return current;

        fields.Add(field);
        return incoming;
    }
}
