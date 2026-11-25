using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Catalog;

/// <summary>
/// Writes Discogs responses into the catalog graph. Idempotent: upserting the same document twice
/// leaves the same rows. Every full document also lands as a <see cref="CatalogRawSnapshot"/> (one
/// row per distinct payload). Credits, labels and listing entries create <see cref="CatalogFetchState.Listed"/>
/// stubs for the artists, labels, masters and releases they name, so a Discogs-only release is
/// browsable before it is read in full. Child collections of a re-read resource are replaced, not merged.
/// </summary>
public interface ICatalogIngestor
{
    Task<CatalogArtist> UpsertArtistAsync(DiscogsDocument<DiscogsArtist> document, CancellationToken ct = default);
    Task<CatalogLabel> UpsertLabelAsync(DiscogsDocument<DiscogsLabel> document, CancellationToken ct = default);
    Task<CatalogMaster> UpsertMasterAsync(DiscogsDocument<DiscogsMaster> document, CancellationToken ct = default);
    Task<CatalogRelease> UpsertReleaseAsync(DiscogsDocument<DiscogsRelease> document, CancellationToken ct = default);

    /// <param name="complete">The entries are the whole listing: entries no longer listed are removed.</param>
    Task<int> UpsertArtistDiscographyAsync(
        int artistId, IReadOnlyList<DiscogsArtistReleaseEntry> entries, bool complete, CancellationToken ct = default);

    Task<int> UpsertLabelReleasesAsync(
        int labelId, IReadOnlyList<DiscogsLabelReleaseEntry> entries, bool complete, CancellationToken ct = default);

    Task<int> UpsertMasterVersionsAsync(
        int masterId, IReadOnlyList<DiscogsMasterVersion> versions, bool complete, CancellationToken ct = default);

    /// <summary>Records a 404 (or a failure, with <paramref name="error"/>) for a resource, creating its stub if needed.</summary>
    Task MarkUnavailableAsync(CatalogEntityKind kind, long discogsId, string? error, CancellationToken ct = default);
}

/// <summary>
/// Reads a resource from Discogs when the catalog does not have it fresh, and ingests it. "Fresh" is
/// Fetched with <c>RefreshAfterUtc</c> in the future (or <paramref name="force"/> off and not stale).
/// Returns null when Discogs does not have it or the client is disabled. Rate limits propagate as
/// <see cref="Enrichment.ProviderRateLimitedException"/>.
/// </summary>
public interface ICatalogFetcher
{
    Task<CatalogArtist?> EnsureArtistAsync(long discogsId, bool withDiscography, bool force = false, CancellationToken ct = default);
    Task<CatalogLabel?> EnsureLabelAsync(long discogsId, bool withReleases, bool force = false, CancellationToken ct = default);
    Task<CatalogMaster?> EnsureMasterAsync(long discogsId, bool withVersions, bool force = false, CancellationToken ct = default);

    /// <summary>Also ensures the release's master (without versions) when it names one.</summary>
    Task<CatalogRelease?> EnsureReleaseAsync(long discogsId, bool force = false, CancellationToken ct = default);
}

/// <summary>Downloads pending image variants (Discogs:DownloadImages) into Discogs:ImageDirectory.</summary>
public interface ICatalogImageDownloader
{
    /// <returns>
    /// Images attempted this pass (downloaded, failed or not found); 0 only when none was pending
    /// (or downloads are off). A rate limit stops the pass and propagates as
    /// <see cref="Enrichment.ProviderRateLimitedException"/>, leaving that image pending; images
    /// finished before it are saved.
    /// </returns>
    Task<int> DownloadPendingAsync(int maxImages, CancellationToken ct = default);
}

/// <summary>Library artist/album → catalog identity, with auditable decisions (see <see cref="CatalogIdentityDecision"/>).</summary>
public interface ICatalogIdentityService
{
    /// <summary>The accepted/chosen decision for a library artist, or the open candidates.</summary>
    Task<CatalogIdentityView> GetArtistIdentityAsync(Guid ownerId, string artistName, CancellationToken ct = default);

    /// <summary>Resolves (MusicBrainz relation first, then a conservative search) and records the decision.</summary>
    Task<CatalogIdentityView> ResolveArtistAsync(Guid ownerId, string artistName, int? runId, bool dryRun, CancellationToken ct = default);

    /// <summary>Resolves a library album to a Discogs release and fills its songs' Discogs ids (unless dry run).</summary>
    Task<CatalogIdentityView> ResolveAlbumAsync(Guid ownerId, string albumArtist, string album, int? runId, bool dryRun, CancellationToken ct = default);

    /// <summary>A person's pick: supersedes earlier decisions for the subject.</summary>
    Task<CatalogIdentityView> ChooseAsync(Guid ownerId, CatalogIdentitySubject subject, string subjectDisplay, CatalogEntityKind target, long discogsId, string decidedBy, CancellationToken ct = default);

    Task<bool> RejectAsync(Guid ownerId, int decisionId, string decidedBy, CancellationToken ct = default);

    Task<IReadOnlyList<CatalogIdentityDecisionDto>> ListReviewAsync(Guid ownerId, int limit, CancellationToken ct = default);
}

public interface ICatalogBackfillService
{
    /// <summary>Starts a run, or resumes the owner's paused run of that kind.</summary>
    Task<CatalogBackfillRunDto> StartAsync(Guid ownerId, CatalogBackfillKind kind, bool dryRun, string startedBy, CancellationToken ct = default);

    /// <summary>Works one batch of a run from its cursor; pauses on a rate limit or the batch budget.</summary>
    Task<CatalogBackfillRunDto> RunBatchAsync(Guid ownerId, int runId, int batchSize, CancellationToken ct = default);

    /// <summary>Reverts the run's decisions and song changes. Idempotent.</summary>
    Task<CatalogBackfillRunDto?> RollbackAsync(Guid ownerId, int runId, CancellationToken ct = default);

    Task<IReadOnlyList<CatalogBackfillRunDto>> ListRunsAsync(Guid ownerId, int limit, CancellationToken ct = default);
}

/// <summary>Read side of the catalog for the browse pages. Ownership is the caller's (ambient owner filter).</summary>
public interface ICatalogQueryService
{
    Task<CatalogPage<CatalogArtistCardDto>> ListArtistsAsync(CatalogListQuery query, CancellationToken ct = default);
    Task<CatalogArtistDetailDto?> GetArtistAsync(int id, CancellationToken ct = default);
    Task<CatalogPage<CatalogLabelCardDto>> ListLabelsAsync(CatalogListQuery query, CancellationToken ct = default);
    Task<CatalogLabelDetailDto?> GetLabelAsync(int id, CancellationToken ct = default);
    Task<CatalogPage<CatalogMasterCardDto>> ListMastersAsync(CatalogReleaseQuery query, CancellationToken ct = default);
    Task<CatalogMasterDetailDto?> GetMasterAsync(int id, CancellationToken ct = default);
    Task<CatalogPage<CatalogReleaseCardDto>> ListReleasesAsync(CatalogReleaseQuery query, CancellationToken ct = default);
    Task<CatalogReleaseDetailDto?> GetReleaseAsync(int id, CancellationToken ct = default);
    Task<CatalogPage<CatalogTrackRowDto>> ListTracksAsync(CatalogListQuery query, CancellationToken ct = default);
    Task<CatalogPage<CatalogStyleDto>> ListStylesAsync(CatalogListQuery query, CancellationToken ct = default);
    Task<CatalogFacetsDto> GetFacetsAsync(CancellationToken ct = default);

    /// <summary>The catalog artist a library artist name is linked to, for the artist page's link.</summary>
    Task<int?> FindArtistForLibraryNameAsync(string artistName, CancellationToken ct = default);
}

/// <summary>
/// Where a catalog release can actually come from, and queueing it. Queueing only ever goes through a
/// concrete provider edition (Deezer, via <c>ArtistReleaseQueuer</c>): a release Discogs lists but no
/// provider serves is reported unavailable, with its purchase links, and nothing is queued.
/// </summary>
public interface ICatalogAcquisitionService
{
    Task<CatalogAvailabilityDto?> GetAvailabilityAsync(int releaseId, CancellationToken ct = default);
    Task<CatalogAcquireResultDto> AcquireAsync(Guid ownerId, int releaseId, string? providerEditionId, CancellationToken ct = default, int? remainingBudget = null);
}
