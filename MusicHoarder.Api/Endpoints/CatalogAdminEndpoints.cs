using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Endpoints;

/// <summary>Admin-only Discogs catalog identity, ingest and backfill controls.</summary>
public static class CatalogAdminEndpoints
{
    public static IEndpointRouteBuilder MapCatalogAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalog/admin").RequireAdmin().WithTags("Catalog");

        group.MapGet("/status", GetStatus)
            .WithSummary("Discogs client state and catalog row counts.");
        group.MapPost("/ingest", Ingest)
            .WithSummary("Force-fetch a Discogs resource into the catalog.");

        group.MapGet("/identity/review", ListReview)
            .WithSummary("Open identity candidates grouped by subject.");
        group.MapGet("/identity/artist", GetArtistIdentity)
            .WithSummary("Accepted or open candidates for a library artist name.");
        group.MapPost("/identity/artist/resolve", ResolveArtist)
            .WithSummary("Resolve a library artist to a Discogs artist.");
        group.MapPost("/identity/album/resolve", ResolveAlbum)
            .WithSummary("Resolve a library album to a Discogs release.");
        group.MapPost("/identity/choose", Choose)
            .WithSummary("Person's pick: supersedes earlier decisions for the subject.");
        group.MapPost("/identity/{id:int}/reject", Reject)
            .WithSummary("Reject an identity candidate.");

        group.MapPost("/backfill", StartBackfill)
            .WithSummary("Start a backfill run, or resume the owner's paused run of that kind.");
        group.MapPost("/backfill/{id:int}/batch", RunBackfillBatch)
            .WithSummary("Process the next batch of a backfill run.");
        group.MapGet("/backfill/runs", ListBackfillRuns)
            .WithSummary("Recent backfill runs for the caller.");
        group.MapPost("/backfill/{id:int}/rollback", RollbackBackfill)
            .WithSummary("Revert a run's decisions and song changes. Idempotent.");

        return app;
    }

    internal static async Task<IResult> GetStatus(
        IDiscogsClient discogs,
        MusicHoarderDbContext db,
        CancellationToken ct)
    {
        var rl = discogs.LastRateLimit;
        var artists = await db.CatalogArtists.CountAsync(ct);
        var labels = await db.CatalogLabels.CountAsync(ct);
        var masters = await db.CatalogMasters.CountAsync(ct);
        var releases = await db.CatalogReleases.CountAsync(ct);
        var pendingImages = await db.CatalogImages.CountAsync(i => i.Status == CatalogImageStatus.Remote, ct);

        return TypedResults.Ok(new CatalogStatusDto(
            discogs.IsEnabled,
            discogs.IsAuthenticated,
            rl?.Limit,
            rl?.Remaining,
            rl?.ObservedAtUtc,
            artists,
            labels,
            masters,
            releases,
            pendingImages));
    }

    internal static async Task<IResult> Ingest(
        CatalogIngestRequest body,
        ICatalogFetcher fetcher,
        CancellationToken ct)
    {
        object? result = body.Kind switch
        {
            CatalogEntityKind.Artist => await fetcher.EnsureArtistAsync(body.DiscogsId, body.WithChildren, force: true, ct),
            CatalogEntityKind.Label => await fetcher.EnsureLabelAsync(body.DiscogsId, body.WithChildren, force: true, ct),
            CatalogEntityKind.Master => await fetcher.EnsureMasterAsync(body.DiscogsId, body.WithChildren, force: true, ct),
            CatalogEntityKind.Release => await fetcher.EnsureReleaseAsync(body.DiscogsId, force: true, ct),
            _ => null,
        };

        return result is null
            ? TypedResults.NotFound(new { message = "Resource not found or Discogs is disabled." })
            : TypedResults.Ok(new { kind = body.Kind.ToString(), discogsId = body.DiscogsId, ingested = true });
    }

    internal static async Task<IResult> ListReview(
        ICatalogIdentityService identity,
        ICurrentUserAccessor currentUser,
        int? limit,
        CancellationToken ct)
    {
        var items = await identity.ListReviewAsync(currentUser.UserId, limit ?? 100, ct);
        return TypedResults.Ok(items);
    }

    internal static async Task<IResult> GetArtistIdentity(
        string? name,
        ICatalogIdentityService identity,
        ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            return TypedResults.BadRequest(new { message = "name is required" });
        return TypedResults.Ok(await identity.GetArtistIdentityAsync(currentUser.UserId, name.Trim(), ct));
    }

    public sealed record ArtistResolveRequest(string Name, bool DryRun = false);

    internal static async Task<IResult> ResolveArtist(
        ArtistResolveRequest body,
        ICatalogIdentityService identity,
        ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return TypedResults.BadRequest(new { message = "name is required" });
        var view = await identity.ResolveArtistAsync(currentUser.UserId, body.Name.Trim(), runId: null, body.DryRun, ct);
        return TypedResults.Ok(view);
    }

    public sealed record AlbumResolveRequest(string AlbumArtist, string Album, bool DryRun = false);

    internal static async Task<IResult> ResolveAlbum(
        AlbumResolveRequest body,
        ICatalogIdentityService identity,
        ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.AlbumArtist) || string.IsNullOrWhiteSpace(body.Album))
            return TypedResults.BadRequest(new { message = "albumArtist and album are required" });
        var view = await identity.ResolveAlbumAsync(
            currentUser.UserId, body.AlbumArtist.Trim(), body.Album.Trim(), runId: null, body.DryRun, ct);
        return TypedResults.Ok(view);
    }

    internal static async Task<IResult> Choose(
        CatalogChooseRequest body,
        ICatalogIdentityService identity,
        ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.SubjectDisplay) || body.DiscogsId <= 0)
            return TypedResults.BadRequest(new { message = "subjectDisplay and discogsId are required" });

        var decidedBy = currentUser.UserId.ToString("D");
        var view = await identity.ChooseAsync(
            currentUser.UserId, body.Subject, body.SubjectDisplay.Trim(), body.Target, body.DiscogsId, decidedBy, ct);
        return TypedResults.Ok(view);
    }

    internal static async Task<IResult> Reject(
        int id,
        ICatalogIdentityService identity,
        ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        var ok = await identity.RejectAsync(currentUser.UserId, id, currentUser.UserId.ToString("D"), ct);
        return ok ? TypedResults.Ok(new { id, status = "Rejected" }) : TypedResults.NotFound();
    }

    internal static async Task<IResult> StartBackfill(
        CatalogBackfillStartRequest body,
        ICatalogBackfillService backfill,
        ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        var run = await backfill.StartAsync(
            currentUser.UserId, body.Kind, body.DryRun, currentUser.UserId.ToString("D"), ct);
        return TypedResults.Ok(run);
    }

    internal static async Task<IResult> RunBackfillBatch(
        int id,
        int? batchSize,
        ICatalogBackfillService backfill,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        var owned = await db.CatalogBackfillRuns
            .IgnoreQueryFilters()
            .AnyAsync(r => r.Id == id && r.OwnerUserId == currentUser.UserId, ct);
        if (!owned)
            return TypedResults.NotFound();

        try
        {
            var run = await backfill.RunBatchAsync(currentUser.UserId, id, batchSize ?? 50, ct);
            return TypedResults.Ok(run);
        }
        catch (InvalidOperationException)
        {
            return TypedResults.NotFound();
        }
    }

    internal static async Task<IResult> ListBackfillRuns(
        ICatalogBackfillService backfill,
        ICurrentUserAccessor currentUser,
        int? limit,
        CancellationToken ct)
    {
        var runs = await backfill.ListRunsAsync(currentUser.UserId, limit ?? 50, ct);
        return TypedResults.Ok(runs);
    }

    internal static async Task<IResult> RollbackBackfill(
        int id,
        ICatalogBackfillService backfill,
        ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        var run = await backfill.RollbackAsync(currentUser.UserId, id, ct);
        return run is null ? TypedResults.NotFound() : TypedResults.Ok(run);
    }
}
