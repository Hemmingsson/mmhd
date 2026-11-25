using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Options;

/// <summary>
/// Discogs (api.discogs.com) as the catalog authority: artists, labels, masters, release editions,
/// credits, identifiers, formats, styles and images. Like the other integrations it degrades
/// gracefully — with no <see cref="Token"/> the database lookups still work at the anonymous
/// allowance, while search and image downloads (which Discogs only serves to an authenticated
/// caller) are off.
/// <para>
/// Every consumer has its own switch so the catalog can be rolled out in steps: read-only catalog
/// browse first, then the enrichment candidate, then the backfill.
/// </para>
/// </summary>
public class DiscogsOptions
{
    public const string SectionName = "Discogs";

    /// <summary>Master switch for every Discogs request.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Personal access token, sent as <c>Authorization: Discogs token=…</c>. Always from env/secret.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Discogs requires a distinctive User-Agent naming the application.</summary>
    [Required]
    public string UserAgent { get; set; } = "MusicHoarder/1.0 +https://github.com/Jeffreyyvdb/MusicHoarder";

    public string BaseUrl { get; set; } = "https://api.discogs.com/";

    /// <summary>
    /// Requests per minute this process allows itself. Discogs allows 60 authenticated / 25 anonymous
    /// in a moving minute; the default leaves headroom for anything else sharing the token.
    /// </summary>
    [Range(1, 60)]
    public int RequestsPerMinute { get; set; } = 50;

    /// <summary>Requests in flight at once.</summary>
    [Range(1, 8)]
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>Retries of one request after a 429 or a 5xx before it gives up.</summary>
    [Range(0, 10)]
    public int MaxRetries { get; set; } = 3;

    /// <summary>The longest a single request waits on a 429 before it reports the rate limit instead.</summary>
    [Range(1, 600)]
    public int MaxRetryWaitSeconds { get; set; } = 90;

    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>How long a fetched resource is kept in memory, so an album's tracks share one release read.</summary>
    [Range(0, 1440)]
    public int CacheMinutes { get; set; } = 60;

    /// <summary>Run Discogs as a supporting enrichment candidate (field authority, never identity).</summary>
    public bool EnableEnrichmentProvider { get; set; }

    /// <summary>Offer Discogs tracklists to canonical album reconciliation.</summary>
    public bool EnableTracklistProvider { get; set; }

    /// <summary>Let the catalog backfill run in the background (it can always be started by hand).</summary>
    public bool EnableBackfill { get; set; }

    /// <summary>Minutes between background backfill passes.</summary>
    [Range(1, 10080)]
    public int BackfillIntervalMinutes { get; set; } = 360;

    /// <summary>Songs one backfill pass looks at before it saves its cursor and stops.</summary>
    [Range(1, 100000)]
    public int BackfillBatchSize { get; set; } = 200;

    /// <summary>Download image variants to <see cref="ImageDirectory"/> (needs a token).</summary>
    public bool DownloadImages { get; set; }

    public string ImageDirectory { get; set; } = "/data/discogs-images";

    /// <summary>Confidence at or above which an identity is linked without a person confirming it.</summary>
    [Range(0.5, 1.0)]
    public double AutoAcceptConfidence { get; set; } = 0.9;

    /// <summary>Days before a fetched catalog resource is refreshed.</summary>
    [Range(1, 3650)]
    public int RefreshDays { get; set; } = 30;

    public bool HasToken => !string.IsNullOrWhiteSpace(Token);
}
