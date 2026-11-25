using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Catalog;

// Wire contract of /api/catalog. frontend/src/lib/catalog/types.ts mirrors it field for field
// (System.Text.Json web defaults: camelCase). Responses carry enums as strings; the request enums
// (CatalogEntityKind, CatalogIdentitySubject, CatalogBackfillKind) are string-converted on the type.

public sealed record CatalogPage<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

/// <param name="Q">Case- and accent-insensitive match on the name/title key.</param>
public sealed record CatalogListQuery(string? Q = null, int Page = 1, int PageSize = 50)
{
    public int SafePage => Math.Max(1, Page);
    public int SafePageSize => Math.Clamp(PageSize, 1, 200);
}

/// <param name="Owned">true: only releases with at least one owned song; false: only unowned.</param>
/// <param name="Sort">"year" (default), "-year", "title", "added".</param>
public sealed record CatalogReleaseQuery(
    string? Q = null,
    int? ArtistId = null,
    int? LabelId = null,
    int? MasterId = null,
    string? Style = null,
    string? Format = null,
    string? Country = null,
    int? YearFrom = null,
    int? YearTo = null,
    bool? Owned = null,
    string? Sort = null,
    int Page = 1,
    int PageSize = 50)
{
    public int SafePage => Math.Max(1, Page);
    public int SafePageSize => Math.Clamp(PageSize, 1, 200);
}

public sealed record CatalogImageDto(
    int Id, string Type, string? Url, string? ThumbnailUrl, int? Width, int? Height, bool Local);

public sealed record CatalogUrlDto(string Url, string Kind);

public sealed record CatalogExternalIdDto(string Provider, string ExternalId, string Source);

public sealed record CatalogRefDto(int? Id, long DiscogsId, string Name, string FetchState);

public sealed record CatalogArtistRelationDto(string Kind, CatalogRefDto Artist, bool? Active);

public sealed record CatalogOwnershipDto(int OwnedSongs, int OwnedReleases);

public sealed record CatalogArtistCardDto(
    int Id, long DiscogsId, string Name, string FetchState, string? ThumbnailUrl,
    int ReleaseCount, int OwnedReleaseCount);

public sealed record CatalogArtistDetailDto(
    int Id,
    long DiscogsId,
    string Name,
    string? RealName,
    string? Profile,
    string FetchState,
    string? DataQuality,
    string? DiscogsUri,
    DateTime? FetchedAtUtc,
    IReadOnlyList<string> NameVariations,
    IReadOnlyList<CatalogArtistRelationDto> Aliases,
    IReadOnlyList<CatalogArtistRelationDto> Members,
    IReadOnlyList<CatalogArtistRelationDto> Groups,
    IReadOnlyList<CatalogUrlDto> Urls,
    IReadOnlyList<CatalogImageDto> Images,
    IReadOnlyList<CatalogExternalIdDto> ExternalIds,
    IReadOnlyList<CatalogStyleDto> TopStyles,
    IReadOnlyList<CatalogDiscographyItemDto> Discography,
    int DiscographyTotal,
    CatalogOwnershipDto Ownership,
    IReadOnlyList<string> LibraryArtistNames);

/// <param name="Kind">"master" or "release".</param>
/// <param name="Role">Discogs listing role: Main, Appearance, TrackAppearance, UnofficialRelease, Remix, …</param>
public sealed record CatalogDiscographyItemDto(
    string Kind, int? MasterId, int? ReleaseId, long DiscogsId, string Title, string? ArtistDisplay,
    int? Year, string? Role, string? Format, string? Label, string? CatalogNumber, string? ThumbnailUrl,
    bool Owned);

public sealed record CatalogLabelCardDto(
    int Id, long DiscogsId, string Name, string FetchState, string? ThumbnailUrl, int ReleaseCount);

public sealed record CatalogLabelDetailDto(
    int Id,
    long DiscogsId,
    string Name,
    string? Profile,
    string? ContactInfo,
    string FetchState,
    string? DataQuality,
    string? DiscogsUri,
    CatalogRefDto? Parent,
    IReadOnlyList<CatalogRefDto> Sublabels,
    IReadOnlyList<CatalogUrlDto> Urls,
    IReadOnlyList<CatalogImageDto> Images,
    IReadOnlyList<CatalogDiscographyItemDto> Releases,
    int ReleaseTotal);

public sealed record CatalogMasterCardDto(
    int Id, long DiscogsId, string Title, string? ArtistDisplay, int? Year, string FetchState,
    string? ThumbnailUrl, IReadOnlyList<string> Styles, int VersionCount, bool Owned);

public sealed record CatalogMasterDetailDto(
    int Id,
    long DiscogsId,
    string Title,
    string? ArtistDisplay,
    int? Year,
    string? Notes,
    string FetchState,
    string? DataQuality,
    string? DiscogsUri,
    long? MainReleaseDiscogsId,
    int? MainReleaseId,
    IReadOnlyList<string> Styles,
    IReadOnlyList<CatalogCreditDto> Artists,
    IReadOnlyList<CatalogImageDto> Images,
    IReadOnlyList<CatalogUrlDto> Urls,
    IReadOnlyList<CatalogTrackDto> Tracklist,
    IReadOnlyList<CatalogReleaseCardDto> Versions,
    int VersionTotal,
    CatalogOwnershipDto Ownership);

public sealed record CatalogReleaseCardDto(
    int Id, long DiscogsId, string Title, string? ArtistDisplay, int? Year, string? Country,
    string? Format, string? Label, string? CatalogNumber, string FetchState, string? ThumbnailUrl,
    IReadOnlyList<string> Styles, int? MasterId, bool Owned);

public sealed record CatalogReleaseDetailDto(
    int Id,
    long DiscogsId,
    string Title,
    string? ArtistDisplay,
    int? Year,
    string? Released,
    string? Country,
    string? Status,
    string? Notes,
    string FetchState,
    string? DataQuality,
    string? DiscogsUri,
    DateTime? FetchedAtUtc,
    CatalogRefDto? Master,
    IReadOnlyList<string> Styles,
    IReadOnlyList<CatalogCreditDto> Artists,
    IReadOnlyList<CatalogCreditDto> ExtraArtists,
    IReadOnlyList<CatalogReleaseLabelDto> Labels,
    IReadOnlyList<CatalogReleaseLabelDto> Companies,
    IReadOnlyList<CatalogReleaseLabelDto> Series,
    IReadOnlyList<CatalogFormatDto> Formats,
    IReadOnlyList<CatalogIdentifierDto> Identifiers,
    IReadOnlyList<CatalogTrackDto> Tracklist,
    IReadOnlyList<CatalogImageDto> Images,
    IReadOnlyList<CatalogUrlDto> Urls,
    IReadOnlyList<CatalogExternalIdDto> ExternalIds,
    CatalogOwnershipDto Ownership);

/// <param name="Artist">Null id when the credited artist has no catalog row yet.</param>
public sealed record CatalogCreditDto(
    CatalogRefDto Artist, string? Anv, string? Join, string? Role, string? Tracks);

public sealed record CatalogReleaseLabelDto(
    CatalogRefDto Label, string? CatalogNumber, string? EntityTypeName);

public sealed record CatalogFormatDto(string Name, string? Quantity, string? Text, IReadOnlyList<string> Descriptions);

public sealed record CatalogIdentifierDto(string Type, string Value, string? Description);

/// <param name="Type">Discogs type_: "track", "heading", "index".</param>
public sealed record CatalogTrackDto(
    int Sequence, string? Position, string Type, string Title, string? Duration, int? DurationSeconds,
    int? DiscNumber, int? TrackNumber, IReadOnlyList<CatalogCreditDto> Credits,
    IReadOnlyList<CatalogTrackDto> SubTracks, bool Owned);

public sealed record CatalogTrackRowDto(
    int ReleaseId, string ReleaseTitle, string? ArtistDisplay, int? Year, int Sequence, string? Position,
    string Title, string? Duration, bool Owned);

public sealed record CatalogStyleDto(string Name, int ReleaseCount);

public sealed record CatalogFacetsDto(
    IReadOnlyList<CatalogStyleDto> Styles,
    IReadOnlyList<CatalogFacetValueDto> Formats,
    IReadOnlyList<CatalogFacetValueDto> Countries,
    int? MinYear,
    int? MaxYear);

public sealed record CatalogFacetValueDto(string Value, int Count);

/// <param name="Provider">"Deezer" today; the key a future provider plugs into.</param>
/// <param name="Confidence">0–1 match of the provider edition to this release (title, artist, track count, year, UPC).</param>
public sealed record CatalogProviderEditionDto(
    string Provider, string EditionId, string Title, string? Artist, int? TrackCount, int? Year,
    string? Upc, double Confidence, string? Url);

/// <param name="Kind">"purchase", "streaming", "info".</param>
public sealed record CatalogPurchaseLinkDto(string Name, string Url, string Kind);

/// <param name="Status">"owned", "available", "unavailable".</param>
public sealed record CatalogAvailabilityDto(
    int ReleaseId,
    string Status,
    CatalogOwnershipDto Ownership,
    IReadOnlyList<CatalogProviderEditionDto> Editions,
    IReadOnlyList<CatalogPurchaseLinkDto> PurchaseLinks);

/// <param name="Status">"queued", "already-owned", "unavailable" (HTTP 409), "not-found" (404).</param>
public sealed record CatalogAcquireResultDto(
    string Status,
    int Queued,
    string? ProviderEditionId,
    string? Message,
    IReadOnlyList<CatalogPurchaseLinkDto> PurchaseLinks);

public sealed record CatalogIdentityCandidateDto(
    int DecisionId, string TargetKind, long DiscogsId, string? TargetName, string Method,
    double Confidence, string Status, int? CatalogId, string? ThumbnailUrl);

public sealed record CatalogIdentityView(
    string SubjectKind, string SubjectKey, string SubjectDisplay,
    CatalogIdentityCandidateDto? Accepted, IReadOnlyList<CatalogIdentityCandidateDto> Candidates);

public sealed record CatalogIdentityDecisionDto(
    int Id, string SubjectKind, string SubjectDisplay, string TargetKind, long DiscogsId,
    string? TargetName, string Method, double Confidence, string Status, int? RunId,
    DateTime CreatedAtUtc, DateTime? DecidedAtUtc, string? DecidedBy);

public sealed record CatalogChooseRequest(
    CatalogIdentitySubject Subject, string SubjectDisplay, CatalogEntityKind Target, long DiscogsId);

public sealed record CatalogBackfillStartRequest(CatalogBackfillKind Kind, bool DryRun = true);

public sealed record CatalogBackfillRunDto(
    int Id, string Kind, string Status, bool DryRun, string? Cursor, int Processed, int Linked,
    int Candidates, int Unmatched, int Failed, int SongsUpdated, int RequestsUsed, string? LastError,
    string? StartedBy, DateTime StartedAtUtc, DateTime? UpdatedAtUtc, DateTime? FinishedAtUtc);

public sealed record CatalogIngestRequest(CatalogEntityKind Kind, long DiscogsId, bool WithChildren = true);

/// <summary>Discogs client state for the admin header.</summary>
public sealed record CatalogStatusDto(
    bool Enabled, bool Authenticated, int? RateLimit, int? RateLimitRemaining, DateTime? RateLimitObservedAtUtc,
    int Artists, int Labels, int Masters, int Releases, int PendingImages);
