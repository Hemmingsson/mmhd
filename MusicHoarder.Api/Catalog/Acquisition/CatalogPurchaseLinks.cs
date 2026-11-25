using System.Diagnostics.CodeAnalysis;

namespace MusicHoarder.Api.Catalog.Acquisition;

/// <summary>
/// Classifies catalog URLs into purchase / streaming / info and always appends the Discogs release
/// page plus marketplace listing. Collects from the release, its master, main artists and labels.
/// </summary>
public static class CatalogPurchaseLinks
{
    public const string KindPurchase = "purchase";
    public const string KindStreaming = "streaming";
    public const string KindInfo = "info";

    public static string Classify(string? url)
    {
        if (!TryHost(url, out var host))
            return KindInfo;

        if (IsPurchaseHost(host))
            return KindPurchase;
        if (IsStreamingHost(host))
            return KindStreaming;
        return KindInfo;
    }

    public static string DisplayName(string? url)
    {
        if (!TryHost(url, out var host))
            return "Link";

        if (host.Contains("bandcamp", StringComparison.Ordinal))
            return "Bandcamp";
        if (host.Contains("qobuz", StringComparison.Ordinal))
            return "Qobuz";
        if (host.Contains("7digital", StringComparison.Ordinal))
            return "7digital";
        if (host.Contains("beatport", StringComparison.Ordinal))
            return "Beatport";
        if (host.Contains("juno", StringComparison.Ordinal))
            return "Juno";
        if (host.Contains("boomkat", StringComparison.Ordinal))
            return "Boomkat";
        if (host.Contains("music.apple", StringComparison.Ordinal) || host.Contains("itunes", StringComparison.Ordinal))
            return "Apple Music";
        if (host.Contains("amazon", StringComparison.Ordinal))
            return "Amazon";
        if (host.Contains("spotify", StringComparison.Ordinal))
            return "Spotify";
        if (host.Contains("deezer", StringComparison.Ordinal))
            return "Deezer";
        if (host.Contains("tidal", StringComparison.Ordinal))
            return "Tidal";
        if (host.Contains("youtube", StringComparison.Ordinal) || host.Contains("youtu.be", StringComparison.Ordinal))
            return "YouTube";
        if (host.Contains("soundcloud", StringComparison.Ordinal))
            return "SoundCloud";
        if (host.Contains("discogs", StringComparison.Ordinal))
            return host.Contains("sell", StringComparison.Ordinal) || url!.Contains("/sell/", StringComparison.OrdinalIgnoreCase)
                ? "Discogs Marketplace"
                : "Discogs";
        return host;
    }

    /// <summary>
    /// Builds the deduped purchase/streaming/info link list for a release, always including the
    /// Discogs release page (info) and marketplace (purchase).
    /// </summary>
    public static IReadOnlyList<CatalogPurchaseLinkDto> Build(
        long releaseDiscogsId,
        IEnumerable<string?> urls)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<CatalogPurchaseLinkDto>();

        void Add(string? url, string? name = null, string? kind = null)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;
            var trimmed = url.Trim();
            if (!seen.Add(trimmed))
                return;
            links.Add(new CatalogPurchaseLinkDto(
                name ?? DisplayName(trimmed),
                trimmed,
                kind ?? Classify(trimmed)));
        }

        foreach (var url in urls)
            Add(url);

        Add($"https://www.discogs.com/release/{releaseDiscogsId}", "Discogs", KindInfo);
        Add($"https://www.discogs.com/sell/release/{releaseDiscogsId}", "Discogs Marketplace", KindPurchase);

        return links;
    }

    private static bool IsPurchaseHost(string host) =>
        host.Contains("bandcamp", StringComparison.Ordinal)
        || host.Contains("qobuz", StringComparison.Ordinal)
        || host.Contains("7digital", StringComparison.Ordinal)
        || host.Contains("beatport", StringComparison.Ordinal)
        || host.Contains("juno", StringComparison.Ordinal)
        || host.Contains("boomkat", StringComparison.Ordinal)
        || host.Contains("itunes", StringComparison.Ordinal)
        || host.Contains("music.apple", StringComparison.Ordinal)
        || host.Contains("amazon", StringComparison.Ordinal);

    private static bool IsStreamingHost(string host) =>
        host.Contains("spotify", StringComparison.Ordinal)
        || host.Contains("deezer", StringComparison.Ordinal)
        || host.Contains("tidal", StringComparison.Ordinal)
        || host.Contains("youtube", StringComparison.Ordinal)
        || host.Contains("youtu.be", StringComparison.Ordinal)
        || host.Contains("soundcloud", StringComparison.Ordinal);

    private static bool TryHost(string? url, [NotNullWhen(true)] out string? host)
    {
        host = null;
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;
        host = uri.Host.ToLowerInvariant();
        return host.Length > 0;
    }
}
