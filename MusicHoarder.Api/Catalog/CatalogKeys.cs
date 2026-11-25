using System.Text.RegularExpressions;
using MusicHoarder.Api.Matching;

namespace MusicHoarder.Api.Catalog;

/// <summary>
/// Shared name/title keys for the catalog graph. Builds on
/// <see cref="TitleNormalizer.NormalizeArtistKey"/> (lowercase, diacritics folded, punctuation
/// collapsed) after stripping Discogs' "(2)"-style disambiguator and a trailing "*".
/// </summary>
public static partial class CatalogKeys
{
    /// <summary>Display helper: drops a trailing <c> (N)</c> Discogs disambiguator and a trailing <c>*</c>.</summary>
    public static string StripDisambiguator(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var result = name.Trim();
        // Trailing "*" (an ANV marker) can sit outside the "(N)" suffix — strip both, either order.
        if (result.EndsWith('*'))
            result = result[..^1].TrimEnd();
        result = DisambiguatorSuffix().Replace(result, "");
        if (result.EndsWith('*'))
            result = result[..^1].TrimEnd();
        return result;
    }

    public static string NameKey(string? name) =>
        TitleNormalizer.NormalizeArtistKey(StripDisambiguator(name));

    public static string TitleKey(string? title) =>
        TitleNormalizer.NormalizeArtistKey(StripDisambiguator(title));

    /// <summary>
    /// Whether a barcode identifier value is usable as a UPC/EAN. Discogs sometimes writes the
    /// literal <c>none</c>; those rows are still stored, but must never be treated as a real barcode.
    /// </summary>
    public static bool IsUsableBarcode(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stable identity of a Discogs CDN image URL: the path with the signed first segment removed.
    /// Signatures rotate; the encoded image path under them does not.
    /// </summary>
    public static string StableImageKey(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
            return string.Empty;

        if (!Uri.TryCreate(sourceUrl.Trim(), UriKind.Absolute, out var uri))
            return sourceUrl.Trim();

        var path = uri.AbsolutePath;
        if (path.Length <= 1)
            return path;

        var secondSlash = path.IndexOf('/', 1);
        return secondSlash < 0 ? path : path[secondSlash..];
    }

    [GeneratedRegex(@"\s*\(\d+\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DisambiguatorSuffix();
}
