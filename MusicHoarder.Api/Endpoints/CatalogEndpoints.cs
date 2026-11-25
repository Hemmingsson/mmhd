using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Catalog.Query;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

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
