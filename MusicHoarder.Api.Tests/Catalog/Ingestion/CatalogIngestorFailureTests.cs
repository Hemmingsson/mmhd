using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MusicHoarder.Api.Catalog.Ingestion;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Catalog.Ingestion;

/// <summary>A failed ingest must not disturb what the caller's shared context was already tracking.</summary>
public class CatalogIngestorFailureTests
{
    [Fact]
    public async Task Failed_ingest_keeps_caller_entities_tracked_and_drops_its_own()
    {
        var failSave = new FailingSaveInterceptor(ctx =>
            ctx.ChangeTracker.Entries<CatalogRawSnapshot>().Any(e => e.State == EntityState.Added));
        var dbName = Guid.NewGuid().ToString("N");
        await using var db = NewContext(dbName, failSave);
        var run = new CatalogBackfillRun
        {
            OwnerUserId = Guid.NewGuid(),
            Kind = CatalogBackfillKind.LibraryAlbums,
            Status = CatalogBackfillStatus.Running,
            StartedAtUtc = CatalogIngestionTestKit.Start.UtcDateTime,
            UpdatedAtUtc = CatalogIngestionTestKit.Start.UtcDateTime,
        };
        db.CatalogBackfillRuns.Add(run);
        await db.SaveChangesAsync();
        var ingestor = Create(db);

        await Assert.ThrowsAsync<SimulatedSaveFailure>(() =>
            ingestor.UpsertArtistAsync(CatalogIngestionTestKit.Document<DiscogsArtist>("artist-45-aphex-twin.json")));

        Assert.Equal(EntityState.Unchanged, db.Entry(run).State);
        var stillTracked = Assert.Single(db.ChangeTracker.Entries());
        Assert.Same(run, stillTracked.Entity);

        run.Processed = 7;
        await db.SaveChangesAsync();

        await using var verify = NewContext(dbName);
        Assert.Equal(7, (await verify.CatalogBackfillRuns.SingleAsync()).Processed);
        Assert.False(await verify.CatalogRawSnapshots.AnyAsync());
    }

    [Fact]
    public async Task Failed_ingest_keeps_a_pending_caller_change_pending()
    {
        var failing = false;
        var dbName = Guid.NewGuid().ToString("N");
        await using var db = NewContext(dbName, new FailingSaveInterceptor(_ => failing));
        var run = new CatalogBackfillRun
        {
            OwnerUserId = Guid.NewGuid(),
            StartedAtUtc = CatalogIngestionTestKit.Start.UtcDateTime,
            UpdatedAtUtc = CatalogIngestionTestKit.Start.UtcDateTime,
        };
        db.CatalogBackfillRuns.Add(run);
        await db.SaveChangesAsync();
        run.Failed = 3;

        failing = true;
        await Assert.ThrowsAsync<SimulatedSaveFailure>(() =>
            Create(db).UpsertArtistAsync(CatalogIngestionTestKit.Document<DiscogsArtist>("artist-45-aphex-twin.json")));

        Assert.Equal(EntityState.Modified, db.Entry(run).State);
        Assert.Single(db.ChangeTracker.Entries());

        failing = false;
        await db.SaveChangesAsync();
        await using var verify = NewContext(dbName);
        Assert.Equal(3, (await verify.CatalogBackfillRuns.SingleAsync()).Failed);
        Assert.False(await verify.CatalogArtists.AnyAsync());
    }

    [Fact]
    public async Task Failed_ingest_detaches_a_previously_tracked_row_it_changed_so_partial_edits_are_never_saved()
    {
        var failing = false;
        var dbName = Guid.NewGuid().ToString("N");
        await using var db = NewContext(dbName, new FailingSaveInterceptor(_ => failing));
        var ingestor = Create(db);
        var doc = CatalogIngestionTestKit.Document<DiscogsArtist>("artist-45-aphex-twin.json");
        var artist = await ingestor.UpsertArtistAsync(doc);
        var originalProfile = artist.Profile;
        var originalVariations = artist.NameVariations.Count;
        Assert.True(originalVariations > 1);

        var changed = new DiscogsDocument<DiscogsArtist>(
            doc.Value with { Profile = "partial edit", Namevariations = ["Only One"] },
            doc.RawJson);
        failing = true;
        await Assert.ThrowsAsync<SimulatedSaveFailure>(() => ingestor.UpsertArtistAsync(changed));

        Assert.Equal(EntityState.Detached, db.Entry(artist).State);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);

        failing = false;
        await db.SaveChangesAsync();
        await using var verify = NewContext(dbName);
        var stored = await verify.CatalogArtists.Include(a => a.NameVariations).SingleAsync(a => a.DiscogsId == 45);
        Assert.Equal(originalProfile, stored.Profile);
        Assert.Equal(originalVariations, stored.NameVariations.Count);
    }

    private static CatalogIngestor Create(MusicHoarderDbContext db) =>
        new(db, CatalogIngestionTestKit.Clock(), CatalogIngestionTestKit.Monitor(CatalogIngestionTestKit.Options()));

    private static MusicHoarderDbContext NewContext(string dbName, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(interceptors)
            .Options);

    private sealed class SimulatedSaveFailure() : Exception("simulated unique violation");

    private sealed class FailingSaveInterceptor(Func<DbContext, bool> shouldFail) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } ctx && shouldFail(ctx))
                throw new SimulatedSaveFailure();
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
