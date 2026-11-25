using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Catalog.Backfill;
using MusicHoarder.Api.Catalog.Identity;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Catalog.Identity;

namespace MusicHoarder.Api.Tests.Catalog.Backfill;

public class CatalogBackfillServiceTests
{
    [Fact]
    public async Task Start_ResumesPausedRunOfSameKind()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        var paused = new CatalogBackfillRun
        {
            OwnerUserId = owner,
            Kind = CatalogBackfillKind.LibraryAlbums,
            Status = CatalogBackfillStatus.Paused,
            Cursor = "aaa",
            DryRun = true,
            StartedAtUtc = DateTime.UtcNow.AddHours(-1),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            StartedBy = "system",
        };
        db.CatalogBackfillRuns.Add(paused);
        await db.SaveChangesAsync();

        var sut = CreateSut(db);
        var dto = await sut.StartAsync(owner, CatalogBackfillKind.LibraryAlbums, dryRun: false, "admin");

        Assert.Equal(paused.Id, dto.Id);
        Assert.Equal(nameof(CatalogBackfillStatus.Running), dto.Status);
        Assert.Equal("aaa", dto.Cursor);
        Assert.Single(await db.CatalogBackfillRuns.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task RunBatch_ResumesFromCursor_AndCompletesWhenExhausted()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        AddSong(db, owner, "Alpha", "A1", "t1");
        AddSong(db, owner, "Beta", "B1", "t2");
        AddSong(db, owner, "Gamma", "G1", "t3");
        await db.SaveChangesAsync();

        var mb = new FakeMusicBrainz();
        // No MB links → unmatched, but still advances cursor.
        var identity = new CatalogIdentityService(
            db, new FakeIdentityDiscogsClient { IsAuthenticated = false }, new FakeCatalogFetcher(), mb,
            Microsoft.Extensions.Options.Options.Create(new DiscogsOptions { AutoAcceptConfidence = 0.9 }));
        var sut = new CatalogBackfillService(db, identity, new FakeCatalogFetcher(), new FakeImages(), new FakeIdentityDiscogsClient());

        var started = await sut.StartAsync(owner, CatalogBackfillKind.LibraryAlbums, dryRun: true, "admin");
        var mid = await sut.RunBatchAsync(owner, started.Id, batchSize: 2);
        Assert.Equal(2, mid.Processed);
        Assert.False(string.IsNullOrEmpty(mid.Cursor));
        Assert.Equal(nameof(CatalogBackfillStatus.Running), mid.Status);

        var done = await sut.RunBatchAsync(owner, started.Id, batchSize: 10);
        Assert.Equal(3, done.Processed);
        Assert.Equal(nameof(CatalogBackfillStatus.Completed), done.Status);
    }

    [Fact]
    public async Task RunBatch_RateLimit_PausesWithLastError()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        AddSong(db, owner, "Aphex Twin", "SAW", "Xtal");
        await db.SaveChangesAsync();

        var discogs = new FakeIdentityDiscogsClient
        {
            IsAuthenticated = true,
            ThrowOnSearch = new ProviderRateLimitedException(TimeSpan.FromSeconds(30)),
        };
        var identity = new CatalogIdentityService(
            db, discogs, new FakeCatalogFetcher(), new FakeMusicBrainz(),
            Microsoft.Extensions.Options.Options.Create(new DiscogsOptions { AutoAcceptConfidence = 0.9 }));
        var sut = new CatalogBackfillService(db, identity, new FakeCatalogFetcher(), new FakeImages(), new FakeIdentityDiscogsClient());

        var started = await sut.StartAsync(owner, CatalogBackfillKind.LibraryAlbums, dryRun: false, "admin");
        var paused = await sut.RunBatchAsync(owner, started.Id, batchSize: 5);

        Assert.Equal(nameof(CatalogBackfillStatus.Paused), paused.Status);
        Assert.False(string.IsNullOrWhiteSpace(paused.LastError));
    }

    [Fact]
    public async Task Rollback_RestoresValues_Idempotent_LeavesUserEdits()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        var song = AddSong(db, owner, "Aphex Twin", "SAW", "Xtal");
        await db.SaveChangesAsync();

        var mb = new FakeMusicBrainz();
        mb.ReleaseRels["will-set"] =
            [new MusicBrainzUrlRelation("discogs", "https://www.discogs.com/release/111")];
        song.MusicBrainzReleaseId = "will-set";
        await db.SaveChangesAsync();

        var fetcher = new FakeCatalogFetcher();
        var identity = new CatalogIdentityService(
            db, new FakeIdentityDiscogsClient { IsAuthenticated = false }, fetcher, mb,
            Microsoft.Extensions.Options.Options.Create(new DiscogsOptions { AutoAcceptConfidence = 0.9 }));
        var sut = new CatalogBackfillService(db, identity, fetcher, new FakeImages(), new FakeIdentityDiscogsClient());

        var run = await sut.StartAsync(owner, CatalogBackfillKind.LibraryAlbums, dryRun: false, "admin");
        await sut.RunBatchAsync(owner, run.Id, batchSize: 10);

        await db.Entry(song).ReloadAsync();
        Assert.Equal(111, song.DiscogsReleaseId);

        // User edit after the run — should be left alone.
        song.DiscogsReleaseId = 999;
        await db.SaveChangesAsync();

        var rolled = await sut.RollbackAsync(owner, run.Id);
        Assert.NotNull(rolled);
        Assert.Equal(nameof(CatalogBackfillStatus.RolledBack), rolled!.Status);

        await db.Entry(song).ReloadAsync();
        Assert.Equal(999, song.DiscogsReleaseId); // user edit kept
        Assert.All(await db.SongMetadataChanges.Where(c => c.Source == $"discogs-run:{run.Id}").ToListAsync(),
            c => Assert.NotNull(c.RevertedAtUtc));

        var again = await sut.RollbackAsync(owner, run.Id);
        Assert.Equal(nameof(CatalogBackfillStatus.RolledBack), again!.Status);
    }

    [Fact]
    public async Task Rollback_RestoresWhenValueUnchanged()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        var song = AddSong(db, owner, "Aphex Twin", "SAW", "Xtal", releaseMbid: "mb");
        await db.SaveChangesAsync();

        var mb = new FakeMusicBrainz();
        mb.ReleaseRels["mb"] =
            [new MusicBrainzUrlRelation("discogs", "https://www.discogs.com/release/222")];
        var fetcher = new FakeCatalogFetcher();
        var identity = new CatalogIdentityService(
            db, new FakeIdentityDiscogsClient { IsAuthenticated = false }, fetcher, mb,
            Microsoft.Extensions.Options.Options.Create(new DiscogsOptions()));
        var sut = new CatalogBackfillService(db, identity, fetcher, new FakeImages(), new FakeIdentityDiscogsClient());

        var run = await sut.StartAsync(owner, CatalogBackfillKind.LibraryAlbums, dryRun: false, "admin");
        await sut.RunBatchAsync(owner, run.Id, 5);
        await db.Entry(song).ReloadAsync();
        Assert.Equal(222, song.DiscogsReleaseId);

        await sut.RollbackAsync(owner, run.Id);
        await db.Entry(song).ReloadAsync();
        Assert.Null(song.DiscogsReleaseId);
    }

    [Fact]
    public async Task TenantIsolation_OwnerARunDoesNotTouchOwnerBSongs()
    {
        await using var db = NewDb();
        var ownerA = TestUsers.OwnerId;
        var ownerB = TestUsers.FriendId;
        var songA = AddSong(db, ownerA, "Aphex Twin", "SAW", "Xtal", releaseMbid: "mb-a");
        var songB = AddSong(db, ownerB, "Aphex Twin", "SAW", "Xtal", releaseMbid: "mb-b");
        await db.SaveChangesAsync();

        var mb = new FakeMusicBrainz();
        mb.ReleaseRels["mb-a"] =
            [new MusicBrainzUrlRelation("discogs", "https://www.discogs.com/release/1")];
        mb.ReleaseRels["mb-b"] =
            [new MusicBrainzUrlRelation("discogs", "https://www.discogs.com/release/2")];

        var fetcher = new FakeCatalogFetcher();
        var identity = new CatalogIdentityService(
            db, new FakeIdentityDiscogsClient { IsAuthenticated = false }, fetcher, mb,
            Microsoft.Extensions.Options.Options.Create(new DiscogsOptions()));
        var sut = new CatalogBackfillService(db, identity, fetcher, new FakeImages(), new FakeIdentityDiscogsClient());

        var run = await sut.StartAsync(ownerA, CatalogBackfillKind.LibraryAlbums, dryRun: false, "admin");
        await sut.RunBatchAsync(ownerA, run.Id, 20);

        await db.Entry(songA).ReloadAsync();
        await db.Entry(songB).ReloadAsync();
        Assert.Equal(1, songA.DiscogsReleaseId);
        Assert.Null(songB.DiscogsReleaseId);

        Assert.All(await db.CatalogIdentityDecisions.IgnoreQueryFilters().ToListAsync(),
            d => Assert.Equal(ownerA, d.OwnerUserId));
    }

    [Fact]
    public async Task RunBatch_AddsTheDiscogsRequestsItSent()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        AddSong(db, owner, "Alpha", "A1", "t1");
        AddSong(db, owner, "Beta", "B1", "t2");
        await db.SaveChangesAsync();

        var discogs = new FakeIdentityDiscogsClient { RequestsSent = 40 };
        var identity = new CatalogIdentityService(
            db, discogs, new FakeCatalogFetcher(), new FakeMusicBrainz(),
            Microsoft.Extensions.Options.Options.Create(new DiscogsOptions()));
        var sut = new CatalogBackfillService(db, identity, new FakeCatalogFetcher(), new FakeImages(), discogs);

        var run = await sut.StartAsync(owner, CatalogBackfillKind.LibraryAlbums, dryRun: true, "admin");
        var first = await sut.RunBatchAsync(owner, run.Id, batchSize: 1);
        var second = await sut.RunBatchAsync(owner, run.Id, batchSize: 10);

        Assert.Equal(1, first.RequestsUsed);
        Assert.Equal(2, second.RequestsUsed);
    }

    [Fact]
    public async Task RunBatch_PausesWithTheError_WhenABatchFailsOutsideASubject()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        var identity = new CatalogIdentityService(
            db, new FakeIdentityDiscogsClient(), new FakeCatalogFetcher(), new FakeMusicBrainz(),
            Microsoft.Extensions.Options.Options.Create(new DiscogsOptions()));
        var sut = new CatalogBackfillService(
            db, identity, new FakeCatalogFetcher(),
            new FakeImages { Throw = new IOException("Read-only file system") }, new FakeIdentityDiscogsClient());

        var run = await sut.StartAsync(owner, CatalogBackfillKind.Images, dryRun: false, "admin");
        var paused = await sut.RunBatchAsync(owner, run.Id, batchSize: 5);

        Assert.Equal(nameof(CatalogBackfillStatus.Paused), paused.Status);
        Assert.Equal("Read-only file system", paused.LastError);
        var stored = await db.CatalogBackfillRuns.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(CatalogBackfillStatus.Paused, stored.Status);
    }

    private static CatalogBackfillService CreateSut(MusicHoarderDbContext db)
    {
        var identity = new CatalogIdentityService(
            db, new FakeIdentityDiscogsClient { IsAuthenticated = false }, new FakeCatalogFetcher(),
            new FakeMusicBrainz(), Microsoft.Extensions.Options.Options.Create(new DiscogsOptions()));
        return new CatalogBackfillService(db, identity, new FakeCatalogFetcher(), new FakeImages(), new FakeIdentityDiscogsClient());
    }

    private static MusicHoarderDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private static SongMetadata AddSong(
        MusicHoarderDbContext db, Guid owner, string artist, string album, string title, string? releaseMbid = null)
    {
        var song = new SongMetadata
        {
            OwnerUserId = owner,
            SourcePath = $"/src/{owner:N}/{album}/{title}-{Guid.NewGuid():N}.flac",
            FileName = $"{title}.flac",
            Extension = ".flac",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Title = title,
            Artist = artist,
            AlbumArtist = artist,
            Album = album,
            MusicBrainzReleaseId = releaseMbid,
            EnrichmentStatus = EnrichmentStatus.Matched,
        };
        db.Songs.Add(song);
        return song;
    }

    private sealed class FakeImages : ICatalogImageDownloader
    {
        public Exception? Throw { get; init; }

        public Task<int> DownloadPendingAsync(int maxImages, CancellationToken ct = default) =>
            Throw is null ? Task.FromResult(0) : Task.FromException<int>(Throw);
    }
}
