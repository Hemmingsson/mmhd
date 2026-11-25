using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MusicHoarder.Api.Discogs;

// Wire shapes of the Discogs database API (https://www.discogs.com/developers/#page:database), as
// served on 2026-11-25 — see MusicHoarder.Api.Tests/Fixtures/Discogs for the responses they were
// written against. Read with DiscogsJson.Options (snake_case). Marketplace, community and user_data
// fields are deliberately absent: the API terms restrict them and nothing here may store them.

public sealed record DiscogsImage(
    string? Type,
    string? Uri,
    string? ResourceUrl,
    string? Uri150,
    int Width,
    int Height);

/// <summary>An artist as another resource names it: a release credit, an alias, a member or a group.</summary>
public sealed record DiscogsArtistRef(
    long Id,
    string? Name,
    string? Anv = null,
    string? Join = null,
    string? Role = null,
    string? Tracks = null,
    string? ResourceUrl = null,
    string? ThumbnailUrl = null,
    bool? Active = null);

public sealed record DiscogsArtist(
    long Id,
    string Name,
    string? Realname = null,
    string? Profile = null,
    string? Uri = null,
    string? ResourceUrl = null,
    string? DataQuality = null,
    IReadOnlyList<string>? Urls = null,
    IReadOnlyList<string>? Namevariations = null,
    IReadOnlyList<DiscogsArtistRef>? Aliases = null,
    IReadOnlyList<DiscogsArtistRef>? Members = null,
    IReadOnlyList<DiscogsArtistRef>? Groups = null,
    IReadOnlyList<DiscogsImage>? Images = null);

/// <summary>A label or company as a release names it, with the catalog number it gives there.</summary>
public sealed record DiscogsLabelRef(
    long Id,
    string? Name,
    string? Catno = null,
    string? EntityType = null,
    string? EntityTypeName = null,
    string? ResourceUrl = null,
    string? ThumbnailUrl = null);

public sealed record DiscogsLabel(
    long Id,
    string Name,
    string? Profile = null,
    string? ContactInfo = null,
    string? Uri = null,
    string? ResourceUrl = null,
    string? DataQuality = null,
    IReadOnlyList<string>? Urls = null,
    DiscogsLabelRef? ParentLabel = null,
    IReadOnlyList<DiscogsLabelRef>? Sublabels = null,
    IReadOnlyList<DiscogsImage>? Images = null);

public sealed record DiscogsFormat(
    string? Name,
    string? Qty = null,
    string? Text = null,
    IReadOnlyList<string>? Descriptions = null);

public sealed record DiscogsIdentifier(string? Type, string? Value, string? Description = null);

/// <summary>A tracklist row: a track, an index track (with <see cref="SubTracks"/>) or a heading.</summary>
public sealed record DiscogsTrack(
    string? Position,
    [property: JsonPropertyName("type_")] string? Type,
    string? Title,
    string? Duration = null,
    IReadOnlyList<DiscogsArtistRef>? Artists = null,
    IReadOnlyList<DiscogsArtistRef>? Extraartists = null,
    IReadOnlyList<DiscogsTrack>? SubTracks = null);

public sealed record DiscogsRelease(
    long Id,
    string Title,
    string? Status = null,
    [property: JsonConverter(typeof(DiscogsFlexibleIntConverter))] int? Year = null,
    string? Country = null,
    string? Released = null,
    string? Notes = null,
    string? DataQuality = null,
    long? MasterId = null,
    string? Uri = null,
    string? ResourceUrl = null,
    string? ArtistsSort = null,
    string? Thumb = null,
    IReadOnlyList<DiscogsArtistRef>? Artists = null,
    IReadOnlyList<DiscogsArtistRef>? Extraartists = null,
    IReadOnlyList<DiscogsLabelRef>? Labels = null,
    IReadOnlyList<DiscogsLabelRef>? Companies = null,
    IReadOnlyList<DiscogsLabelRef>? Series = null,
    IReadOnlyList<DiscogsFormat>? Formats = null,
    IReadOnlyList<DiscogsIdentifier>? Identifiers = null,
    IReadOnlyList<string>? Genres = null,
    IReadOnlyList<string>? Styles = null,
    IReadOnlyList<DiscogsTrack>? Tracklist = null,
    IReadOnlyList<DiscogsImage>? Images = null);

public sealed record DiscogsMaster(
    long Id,
    string Title,
    [property: JsonConverter(typeof(DiscogsFlexibleIntConverter))] int? Year = null,
    long? MainRelease = null,
    long? MostRecentRelease = null,
    string? Uri = null,
    string? ResourceUrl = null,
    string? Notes = null,
    string? DataQuality = null,
    IReadOnlyList<DiscogsArtistRef>? Artists = null,
    IReadOnlyList<string>? Genres = null,
    IReadOnlyList<string>? Styles = null,
    IReadOnlyList<DiscogsTrack>? Tracklist = null,
    IReadOnlyList<DiscogsImage>? Images = null);

public sealed record DiscogsPagination(int Page, int Pages, int PerPage, int Items);

/// <summary>One page of a paginated list, normalized from the list's own property name.</summary>
public sealed record DiscogsPage<T>(DiscogsPagination Pagination, IReadOnlyList<T> Items);

/// <summary>An entry of <c>/artists/{id}/releases</c>: a master or a release, with the artist's role on it.</summary>
public sealed record DiscogsArtistReleaseEntry(
    long Id,
    string? Type,
    string? Title,
    string? Artist = null,
    string? Role = null,
    [property: JsonConverter(typeof(DiscogsFlexibleIntConverter))] int? Year = null,
    long? MainRelease = null,
    string? Format = null,
    string? Label = null,
    string? Status = null,
    string? Thumb = null,
    string? ResourceUrl = null);

/// <summary>An entry of <c>/labels/{id}/releases</c>.</summary>
public sealed record DiscogsLabelReleaseEntry(
    long Id,
    string? Title,
    string? Artist = null,
    string? Catno = null,
    string? Format = null,
    [property: JsonConverter(typeof(DiscogsFlexibleIntConverter))] int? Year = null,
    string? Status = null,
    string? Thumb = null,
    string? ResourceUrl = null);

/// <summary>An entry of <c>/masters/{id}/versions</c>: one edition of the master.</summary>
public sealed record DiscogsMasterVersion(
    long Id,
    string? Title,
    string? Label = null,
    string? Country = null,
    string? Format = null,
    IReadOnlyList<string>? MajorFormats = null,
    string? Catno = null,
    string? Released = null,
    string? Status = null,
    string? Thumb = null,
    string? ResourceUrl = null);

public sealed record DiscogsSearchResult(
    long Id,
    string? Type,
    string? Title,
    string? Country = null,
    [property: JsonConverter(typeof(DiscogsFlexibleIntConverter))] int? Year = null,
    IReadOnlyList<string>? Format = null,
    IReadOnlyList<string>? Label = null,
    string? Catno = null,
    IReadOnlyList<string>? Barcode = null,
    long? MasterId = null,
    IReadOnlyList<string>? Genre = null,
    IReadOnlyList<string>? Style = null,
    string? Thumb = null,
    string? CoverImage = null,
    string? ResourceUrl = null,
    string? Uri = null);

/// <summary>
/// The search parameters Discogs documents for <c>/database/search</c>. Unset ones are left off the
/// query. Search needs an authenticated caller.
/// </summary>
public sealed record DiscogsSearchQuery(
    string? Type = null,
    string? Query = null,
    string? Title = null,
    string? ReleaseTitle = null,
    string? Artist = null,
    string? Anv = null,
    string? Credit = null,
    string? Label = null,
    string? Genre = null,
    string? Style = null,
    string? Country = null,
    int? Year = null,
    string? Format = null,
    string? Catno = null,
    string? Barcode = null,
    string? Track = null);

/// <summary>Filters <c>/masters/{id}/versions</c> accepts.</summary>
public sealed record DiscogsVersionFilter(string? Format = null, string? Label = null, string? Released = null, string? Country = null);

/// <summary>A fetched resource with the exact JSON it was read from, for the raw snapshot.</summary>
public sealed record DiscogsDocument<T>(T Value, string RawJson);

public sealed record DiscogsImageBytes(byte[] Content, string? ContentType);

/// <summary>What the last response said about the allowance (the X-Discogs-Ratelimit headers).</summary>
public sealed record DiscogsRateLimitSnapshot(int? Limit, int? Used, int? Remaining, DateTime ObservedAtUtc);

/// <summary>
/// The Discogs database API. Not-found answers null; a rate limit that outlasts the retry budget
/// throws <see cref="Enrichment.ProviderRateLimitedException"/>; any other failure throws
/// <see cref="DiscogsApiException"/>.
/// </summary>
public interface IDiscogsClient
{
    /// <summary>Requests may be made at all (the master switch is on).</summary>
    bool IsEnabled { get; }

    /// <summary>A token is configured, so search and image downloads are available.</summary>
    bool IsAuthenticated { get; }

    /// <summary>The newest rate-limit headers seen, or null before the first response.</summary>
    DiscogsRateLimitSnapshot? LastRateLimit { get; }

    /// <summary>HTTP requests sent since start, retries included; cache hits are not requests.</summary>
    long RequestsSent => 0;

    Task<DiscogsDocument<DiscogsArtist>?> GetArtistAsync(long id, CancellationToken ct = default);
    Task<DiscogsDocument<DiscogsLabel>?> GetLabelAsync(long id, CancellationToken ct = default);
    Task<DiscogsDocument<DiscogsMaster>?> GetMasterAsync(long id, CancellationToken ct = default);
    Task<DiscogsDocument<DiscogsRelease>?> GetReleaseAsync(long id, CancellationToken ct = default);

    Task<DiscogsPage<DiscogsArtistReleaseEntry>?> GetArtistReleasesAsync(
        long id, int page = 1, int perPage = 100, CancellationToken ct = default);

    Task<DiscogsPage<DiscogsLabelReleaseEntry>?> GetLabelReleasesAsync(
        long id, int page = 1, int perPage = 100, CancellationToken ct = default);

    Task<DiscogsPage<DiscogsMasterVersion>?> GetMasterVersionsAsync(
        long id, int page = 1, int perPage = 100, DiscogsVersionFilter? filter = null, CancellationToken ct = default);

    /// <summary>Database search. Answers an empty page when no token is configured.</summary>
    Task<DiscogsPage<DiscogsSearchResult>> SearchAsync(
        DiscogsSearchQuery query, int page = 1, int perPage = 25, CancellationToken ct = default);

    /// <summary>Downloads an image by the signed URL a response gave (never constructed or altered).</summary>
    Task<DiscogsImageBytes?> DownloadImageAsync(string signedUrl, CancellationToken ct = default);
}

public sealed class DiscogsApiException(string message, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}

public static class DiscogsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

/// <summary>
/// Discogs writes a year as a number on resources and as a string in search results, and a release
/// with no known year as 0 or "". Both read as an int, 0 and blanks as null.
/// </summary>
public sealed class DiscogsFlexibleIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number when reader.TryGetInt32(out var n):
                return n > 0 ? n : null;
            case JsonTokenType.String:
                var text = reader.GetString();
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                    ? parsed
                    : null;
            case JsonTokenType.Number:
                return null;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is int v) writer.WriteNumberValue(v);
        else writer.WriteNullValue();
    }
}

public enum DiscogsResourceKind
{
    Artist,
    Label,
    Master,
    Release,
}

/// <summary>
/// Reads a Discogs web or API URL — as MusicBrainz url relationships carry them
/// (<c>https://www.discogs.com/release/123-Slug</c>, <c>/master/565</c>, <c>/artist/45</c>) — into a
/// resource kind and id. Anything else is not a Discogs reference.
/// </summary>
public static partial class DiscogsUrl
{
    public static bool TryParse(string? url, out DiscogsResourceKind kind, out long id)
    {
        kind = default;
        id = 0;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;
        var host = uri.Host.ToLowerInvariant();
        if (host != "discogs.com" && !host.EndsWith(".discogs.com", StringComparison.Ordinal))
            return false;

        var match = Path().Match(uri.AbsolutePath);
        if (!match.Success || !long.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out id))
            return false;

        kind = match.Groups["kind"].Value.ToLowerInvariant() switch
        {
            "artist" or "artists" => DiscogsResourceKind.Artist,
            "label" or "labels" => DiscogsResourceKind.Label,
            "master" or "masters" => DiscogsResourceKind.Master,
            _ => DiscogsResourceKind.Release,
        };
        return id > 0;
    }

    // An optional two-letter locale segment ("/fr/release/…") and an optional slug after the id.
    [GeneratedRegex(@"^/(?:[a-z]{2}/)?(?<kind>artist|artists|label|labels|master|masters|release|releases)/(?<id>\d+)(?:-[^/]*)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex Path();
}
