using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Catalog.Query;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Artists;
using MusicHoarder.Api.Catalog.Acquisition;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Enrichment;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Admin browse + acquire surface for the Discogs catalog graph under <c>/api/catalog</c>.
/// DI and mapping into the app are wired by the parent (do not edit ServiceCollectionExtensions).
/// </summary>
public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalog").RequireAdmin().WithTags("Catalog");

        group.MapGet("/artists", ListArtists);
        group.MapGet("/artists/{id:int}", GetArtist);
        group.MapGet("/labels", ListLabels);
        group.MapGet("/labels/{id:int}", GetLabel);
        group.MapGet("/discogs/labels/{id:long:min(1)}/releases", ListDiscogsLabelReleases);
        group.MapPost("/discogs/releases/{id:long:min(1)}/resolve", ResolveDiscogsRelease);
        group.MapPost("/discogs/releases/queue", QueueDiscogsReleases);
        group.MapGet("/masters", ListMasters);
        group.MapGet("/masters/{id:int}", GetMaster);
        group.MapGet("/releases", ListReleases);
        group.MapGet("/releases/{id:int}", GetRelease);
        group.MapGet("/tracks", ListTracks);
        group.MapGet("/styles", ListStyles);
        group.MapGet("/facets", GetFacets);
        group.MapGet("/library-artist", FindLibraryArtist);
        group.MapGet("/library-profile", LibraryProfiles.GetAsync);
        group.MapGet("/releases/{id:int}/availability", GetAvailability);
        group.MapPost("/releases/{id:int}/acquire", Acquire);
        group.MapGet("/images/{id:int}", GetImage);

        return app;
    }

    internal static async Task<IResult> ListArtists(
        [AsParameters] CatalogListQuery query, ICatalogQueryService catalog, CancellationToken ct) =>
        Results.Ok(await catalog.ListArtistsAsync(query, ct));

    internal static async Task<IResult> GetArtist(int id, ICatalogQueryService catalog, CancellationToken ct)
    {
        var dto = await catalog.GetArtistAsync(id, ct);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    internal static async Task<IResult> ListLabels(
        [AsParameters] CatalogListQuery query, ICatalogQueryService catalog, CancellationToken ct) =>
        Results.Ok(await catalog.ListLabelsAsync(query, ct));

    internal static async Task<IResult> GetLabel(int id, ICatalogQueryService catalog, CancellationToken ct)
    {
        var dto = await catalog.GetLabelAsync(id, ct);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    internal static async Task<IResult> ListDiscogsLabelReleases(long id, int page, IDiscogsClient client, MusicHoarderDbContext db, CancellationToken ct)
    {
        if (page < 1) return Results.BadRequest();
        DiscogsPage<DiscogsLabelReleaseEntry>? result;
        try
        {
            result = await client.GetLabelReleasesAsync(id, page, 100, ct);
        }
        catch (Exception ex) when (ex is DiscogsApiException or ProviderRateLimitedException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        if (result == null) return Results.NotFound();
        var ids = result.Items.Select(e => e.Id).Distinct().ToArray();
        var local = await db.CatalogReleases.Where(r => ids.Contains(r.DiscogsId))
            .ToDictionaryAsync(r => r.DiscogsId, r => (int?)r.Id, ct);
        var owned = (await db.Songs.Where(s => s.DeletedAtUtc == null && s.DiscogsReleaseId.HasValue
                && ids.Contains(s.DiscogsReleaseId.Value)).Select(s => s.DiscogsReleaseId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        var items = result.Items.Select(e => new CatalogDiscographyItemDto("release", null,
            local.GetValueOrDefault(e.Id), e.Id, e.Title ?? "Untitled", e.Artist, e.Year, null,
            e.Format, null, e.Catno, e.Thumb, owned.Contains(e.Id)));
        return Results.Ok(new DiscogsPage<CatalogDiscographyItemDto>(result.Pagination, items.ToArray()));
    }

    internal static async Task<IResult> ResolveDiscogsRelease(long id, ICatalogFetcher fetcher, CancellationToken ct)
    {
        var release = await fetcher.EnsureReleaseAsync(id, ct: ct);
        return release == null ? Results.NotFound() : Results.Ok(new { release.Id });
    }

    internal static async Task<IResult> QueueDiscogsReleases(long[] releaseIds, ICatalogFetcher fetcher,
        ICatalogAcquisitionService acquisition, ArtistReleaseQueuer queuer, MusicHoarderDbContext db, ICurrentUserAccessor user, JobManager jobs,
        IOptions<MusicEnricherOptions> options, CancellationToken ct)
    {
        if (!options.Value.EnableWishlistDownloads)
            return Results.Conflict(new { message = "Downloads are off on this server." });
        if (releaseIds.Length == 0 || releaseIds.Any(id => id < 1)) return Results.BadRequest();

        var ids = releaseIds.Distinct().ToArray();
        var budget = Math.Max(1, options.Value.ArtistRequestMaxTracks);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var queued = 0;
        var started = false;
        var results = new List<ArtistDiscographyEndpoints.AcquireReleaseResult>();
        foreach (var id in ids.Take(200))
        {
            // Leave time to finish the current provider lookup and return its committed counts.
            if (queued >= budget || elapsed.Elapsed >= TimeSpan.FromSeconds(90)) break;
            var release = await fetcher.EnsureReleaseAsync(id, ct: ct);
            var result = release == null ? null :
                await acquisition.AcquireAsync(user.UserId, release.Id, null, ct, budget - queued);
            if (release != null && result?.Status == "unavailable")
            {
                var edition = await db.CatalogReleases.AsNoTracking().Include(r => r.Tracks)
                    .Include(r => r.Credits).Include(r => r.Formats).SingleAsync(r => r.Id == release.Id, ct);
                var tracks = edition.Tracks.Where(t => t.Type == "track" && !string.IsNullOrWhiteSpace(t.Title))
                    .OrderBy(t => t.Sequence).Select((t, i) => new DeezerAlbumTrackItem(
                        t.DiscNumber ?? 1, t.TrackNumber ?? i + 1, t.Title, (t.DurationSeconds ?? 0) * 1000, null,
                        edition.Credits.Where(c => c.TrackSequence == t.Sequence && c.Kind == CatalogCreditKind.Main)
                            .OrderBy(c => c.Sequence).Select(c => c.Name).FirstOrDefault())).ToList();
                if (tracks.Count > 0 && !string.IsNullOrWhiteSpace(edition.ArtistDisplay))
                {
                    var detail = new DeezerAlbumDetail(id.ToString(), edition.Title, edition.ArtistDisplay,
                        edition.Year, edition.Thumb, tracks);
                    var fallback = await queuer.QueueTracklistAsync(user.UserId, edition.ArtistDisplay, detail,
                        EnrichmentProvider.Discogs, CatalogAcquisitionService.KindFromRelease(edition),
                        WishlistItemOrigin.ArtistRequest, budget - queued, true, ct);
                    result = new("queued", fallback.Queued + fallback.Requeued, null, fallback.Error, [],
                        fallback.AlreadyOwned, fallback.AlreadyQueued, fallback.Capped);
                }
            }
            var count = result?.Queued ?? 0;
            queued += count;
            // Start after the wishlist commit, including when a later edition fails or the caller leaves.
            if (count > 0) started |= jobs.TryStartJob(JobType.Download, out _, out _);
            results.Add(new(id.ToString(), release?.Title, count, 0, result?.AlreadyOwned ?? 0,
                result?.AlreadyQueued ?? 0, result?.Capped ?? 0, result?.Status == "already-owned"
                    ? null : result?.Message ?? (result == null ? "Release not found." : null)));
        }
        return Results.Ok(new { queued, capped = results.Sum(r => r.Capped),
            skippedReleases = ids.Length - results.Count, jobStarted = started, results });
    }

    internal static async Task<IResult> ListMasters(
        [AsParameters] CatalogReleaseQuery query, ICatalogQueryService catalog, CancellationToken ct) =>
        Results.Ok(await catalog.ListMastersAsync(query, ct));

    internal static async Task<IResult> GetMaster(int id, ICatalogQueryService catalog, CancellationToken ct)
    {
        var dto = await catalog.GetMasterAsync(id, ct);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    internal static async Task<IResult> ListReleases(
        [AsParameters] CatalogReleaseQuery query, ICatalogQueryService catalog, CancellationToken ct) =>
        Results.Ok(await catalog.ListReleasesAsync(query, ct));

    internal static async Task<IResult> GetRelease(int id, ICatalogQueryService catalog, CancellationToken ct)
    {
        var dto = await catalog.GetReleaseAsync(id, ct);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    internal static async Task<IResult> ListTracks(
        [AsParameters] CatalogListQuery query, ICatalogQueryService catalog, CancellationToken ct) =>
        Results.Ok(await catalog.ListTracksAsync(query, ct));

    internal static async Task<IResult> ListStyles(
        [AsParameters] CatalogListQuery query, ICatalogQueryService catalog, CancellationToken ct) =>
        Results.Ok(await catalog.ListStylesAsync(query, ct));

    internal static async Task<IResult> GetFacets(ICatalogQueryService catalog, CancellationToken ct) =>
        Results.Ok(await catalog.GetFacetsAsync(ct));

    internal static async Task<IResult> FindLibraryArtist(
        [FromQuery] string? name, ICatalogQueryService catalog, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new { message = "name is required" });
        var id = await catalog.FindArtistForLibraryNameAsync(name.Trim(), ct);
        return id is null ? Results.NotFound() : Results.Ok(new { id });
    }

    internal static async Task<IResult> GetAvailability(
        int id, ICatalogAcquisitionService acquisition, CancellationToken ct)
    {
        var dto = await acquisition.GetAvailabilityAsync(id, ct);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    public sealed record CatalogAcquireRequest(string? ProviderEditionId = null);

    internal static async Task<IResult> Acquire(
        int id,
        CatalogAcquireRequest? body,
        ICatalogAcquisitionService acquisition,
        ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        var result = await acquisition.AcquireAsync(currentUser.UserId, id, body?.ProviderEditionId, ct);
        return result.Status switch
        {
            "not-found" => Results.NotFound(result),
            "unavailable" => Results.Conflict(result),
            _ => Results.Ok(result),
        };
    }

    /// <summary>
    /// Streams a downloaded catalog image from <c>Discogs:ImageDirectory</c> + <see cref="CatalogImage.LocalPath"/>.
    /// Local image URLs on DTOs use <c>/api/catalog/images/{id}</c>.
    /// </summary>
    internal static async Task<IResult> GetImage(
        int id,
        MusicHoarderDbContext db,
        IOptions<DiscogsOptions> options,
        HttpContext http,
        CancellationToken ct)
    {
        var image = await db.CatalogImages.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id, ct);
        if (image is null
            || image.Status != CatalogImageStatus.Downloaded
            || string.IsNullOrWhiteSpace(image.LocalPath))
        {
            return Results.NotFound();
        }

        var root = options.Value.ImageDirectory;
        var fullPath = Path.IsPathRooted(image.LocalPath)
            ? image.LocalPath
            : Path.Combine(root, image.LocalPath);
        if (!File.Exists(fullPath))
            return Results.NotFound();

        http.Response.Headers.CacheControl = "private, max-age=604800";
        var contentType = string.IsNullOrWhiteSpace(image.ContentType) ? "application/octet-stream" : image.ContentType;
        return Results.File(fullPath, contentType: contentType);
    }
}
