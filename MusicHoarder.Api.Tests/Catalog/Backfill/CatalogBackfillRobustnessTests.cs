using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Catalog.Backfill;
using MusicHoarder.Api.Catalog.Identity;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Catalog.Identity;

namespace MusicHoarder.Api.Tests.Catalog.Backfill;

public class CatalogBackfillRobustnessTests
{
    [Fact]
    public async Task RunBatch_KeepsSavingProgress_WhenTheChangeTrackerIsClearedMidSubject()
    {
        var name = Guid.NewGuid().ToString("N");
        await using var db = NewDb(name);
        var owner = TestUsers.OwnerId;
        AddSong(db, owner, "Alpha", "A1", "t1");
        AddSong(db, owner, "Beta", "B1", "t2");
        AddSong(db, owner, "Gamma", "G1", "t3");
        await db.SaveChangesAsync();

        var identity = new ClearingIdentity(db);
        var sut = new CatalogBackfillService(db, identity, new RecordingFetcher(), new SequenceImages(), new FakeIdentityDiscogsClient());

        var run = await sut.StartAsync(owner, CatalogBackfillKind.LibraryAlbums, dryRun: false, "admin");
        await sut.RunBatchAsync(owner, run.Id, batchSize: 10);

        await using var fresh = NewDb(name);
        var stored = await fresh.CatalogBackfillRuns.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(3, stored.Processed);
        Assert.Equal(3, stored.Unmatched);
        Assert.Equal(DiscogsMatchScoring.AlbumSubjectKey("Gamma", "G1"), stored.Cursor);
        Assert.Equal(CatalogBackfillStatus.Completed, stored.Status);
    }

    [Fact]
    public async Task RunBatch_SavesAFailedSubject_WhenTheIdentityClearsTheTrackerAndThrows()
    {
        var name = Guid.NewGuid().ToString("N");
        await using var db = NewDb(name);
        var owner = TestUsers.OwnerId;
        AddSong(db, owner, "Alpha", "A1", "t1");
        AddSong(db, owner, "Beta", "B1", "t2");
        await db.SaveChangesAsync();

        var identity = new ClearingIdentity(db) { ThrowFor = "A1" };
        var sut = new CatalogBackfillService(db, identity, new RecordingFetcher(), new SequenceImages(), new FakeIdentityDiscogsClient());

        var run = await sut.StartAsync(owner, CatalogBackfillKind.LibraryAlbums, dryRun: false, "admin");
        await sut.RunBatchAsync(owner, run.Id, batchSize: 10);

        await using var fresh = NewDb(name);
        var stored = await fresh.CatalogBackfillRuns.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(2, stored.Processed);
        Assert.Equal(1, stored.Failed);
        Assert.Equal(1, stored.Unmatched);
        Assert.Equal(DiscogsMatchScoring.AlbumSubjectKey("Beta", "B1"), stored.Cursor);
    }

    [Fact]
    public async Task RunBatch_CountsOnlySongsNewlyGivenAReleaseId()
    {
        await using var db = NewDb(Guid.NewGuid().ToString("N"));
        var owner = TestUsers.OwnerId;
        AddSong(db, owner, "Aphex Twin", "SAW", "Xtal", releaseMbid: "mb");
        AddSong(db, owner, "Aphex Twin", "SAW", "Tha", releaseMbid: "mb");
        var other = AddSong(db, owner, "Aphex Twin", "Drukqs", "Avril", releaseMbid: "mb2");
        other.DiscogsReleaseId = 77;
        await db.SaveChangesAsync();

        var mb = new FakeMusicBrainz();
        mb.ReleaseRels["mb"] = [new MusicBrainzUrlRelation("discogs", "https://www.discogs.com/release/111")];
        var fetcher = new FakeCatalogFetcher();
        var identity = new CatalogIdentityService(
            db, new FakeIdentityDiscogsClient { IsAuthenticated = false }, fetcher, mb,
            Microsoft.Extensions.Options.Options.Create(new DiscogsOptions()));
        var sut = new CatalogBackfillService(db, identity, fetcher, new SequenceImages(), new FakeIdentityDiscogsClient());

        var run = await sut.StartAsync(owner, CatalogBackfillKind.LibraryAlbums, dryRun: false, "admin");
        var done = await sut.RunBatchAsync(owner, run.Id, batchSize: 10);

        Assert.Equal(1, done.Linked);
        Assert.Equal(2, done.SongsUpdated);
    }

    [Fact]
    public async Task BatchEndpoint_ReturnsNotFound_ForAnotherOwnersRun()
    {
        await using var db = NewDb(Guid.NewGuid().ToString("N"));
        var ownerA = TestUsers.OwnerId;
        AddSong(db, ownerA, "Alpha", "A1", "t1");
        await db.SaveChangesAsync();
        var sut = new CatalogBackfillService(
            db, new ClearingIdentity(db), new RecordingFetcher(), new SequenceImages(), new FakeIdentityDiscogsClient());
        var run = await sut.StartAsync(ownerA, CatalogBackfillKind.LibraryAlbums, dryRun: true, "admin");

        var asB = new TestCurrentUserAccessor(TestCurrentUserAccessor.FriendUser);
        var denied = await CatalogAdminEndpoints.RunBackfillBatch(run.Id, null, sut, db, asB, CancellationToken.None);

        Assert.IsType<NotFound>(denied);
        var stored = await db.CatalogBackfillRuns.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(0, stored.Processed);
        Assert.Equal(CatalogBackfillStatus.Running, stored.Status);

        var asA = new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser);
        var allowed = await CatalogAdminEndpoints.RunBackfillBatch(run.Id, null, sut, db, asA, CancellationToken.None);
        Assert.IsType<Ok<CatalogBackfillRunDto>>(allowed);
    }

    [Fact]
    public async Task RunBatch_RejectsAnotherOwnersRun()
    {
        await using var db = NewDb(Guid.NewGuid().ToString("N"));
        var owner = TestUsers.OwnerId;
        AddSong(db, owner, "Alpha", "A1", "t1");
        await db.SaveChangesAsync();
        var sut = new CatalogBackfillService(
            db, new ClearingIdentity(db), new RecordingFetcher(), new SequenceImages(), new FakeIdentityDiscogsClient());
        var run = await sut.StartAsync(owner, CatalogBackfillKind.LibraryAlbums, dryRun: true, "admin");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RunBatchAsync(TestUsers.FriendId, run.Id, batchSize: 10));

        var stored = await db.CatalogBackfillRuns.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(0, stored.Processed);
        Assert.Equal(CatalogBackfillStatus.Running, stored.Status);
    }

    [Fact]
    public async Task Worker_AdvancesRunningRuns_AndResumesAutoPausedRunsOlderThanTheInterval()
    {
        var name = Guid.NewGuid().ToString("N");
        var interval = TimeSpan.FromMinutes(60);
        var now = DateTime.UtcNow;
        var old = now - interval - TimeSpan.FromMinutes(5);
        int running, autoPausedOld;
        await using (var db = NewDb(name))
        {
            running = AddRun(db, CatalogBackfillStatus.Running, null, now);
            autoPausedOld = AddRun(db, CatalogBackfillStatus.Paused, "429", old);
            AddRun(db, CatalogBackfillStatus.Paused, "Rate limited. Retry after 7200s.", old);
            AddRun(db, CatalogBackfillStatus.Paused, "429", now - TimeSpan.FromMinutes(5));
            AddRun(db, CatalogBackfillStatus.Paused, null, old);
            AddRun(db, CatalogBackfillStatus.Completed, "x", old);
            await db.SaveChangesAsync();
        }

        var backfill = new RecordingBackfill();
        var services = new ServiceCollection()
            .AddDbContext<MusicHoarderDbContext>(o => o.UseInMemoryDatabase(name))
            .AddSingleton<ICatalogBackfillService>(backfill)
            .BuildServiceProvider();
        var opts = new DiscogsOptions { BackfillIntervalMinutes = (int)interval.TotalMinutes, BackfillBatchSize = 7 };
        var worker = new CatalogBackfillWorker(
            services.GetRequiredService<IServiceScopeFactory>(), new StaticOptionsMonitor<DiscogsOptions>(opts),
            NullLogger<CatalogBackfillWorker>.Instance);

        await worker.TickAsync(opts, CancellationToken.None);

        Assert.Equal([running, autoPausedOld], backfill.Calls.Select(c => c.RunId).Order().ToArray());
        Assert.All(backfill.Calls, c => Assert.Equal(7, c.BatchSize));
    }

    [Fact]
    public async Task CatalogResources_RefreshesEveryStaleReleaseThenEveryStaleArtist()
    {
        await using var db = NewDb(Guid.NewGuid().ToString("N"));
        var stale = DateTime.UtcNow.AddDays(-1);
        foreach (var id in new long[] { 100, 200, 300 })
            db.CatalogReleases.Add(new CatalogRelease { DiscogsId = id, FetchState = CatalogFetchState.Fetched, RefreshAfterUtc = stale, Title = $"r{id}" });
        db.CatalogReleases.Add(new CatalogRelease { DiscogsId = 400, FetchState = CatalogFetchState.Fetched, RefreshAfterUtc = DateTime.UtcNow.AddDays(5), Title = "fresh" });
        foreach (var id in new long[] { 50, 150 })
            db.CatalogArtists.Add(new CatalogArtist { DiscogsId = id, FetchState = CatalogFetchState.Fetched, RefreshAfterUtc = stale, Name = $"a{id}" });
        await db.SaveChangesAsync();

        var fetcher = new RecordingFetcher();
        var sut = new CatalogBackfillService(db, new ClearingIdentity(db), fetcher, new SequenceImages(), new FakeIdentityDiscogsClient());
        var run = await sut.StartAsync(TestUsers.OwnerId, CatalogBackfillKind.CatalogResources, dryRun: false, "admin");

        var first = await sut.RunBatchAsync(TestUsers.OwnerId, run.Id, batchSize: 2);
        Assert.Equal("r:200", first.Cursor);
        Assert.Equal(nameof(CatalogBackfillStatus.Running), first.Status);

        var second = await sut.RunBatchAsync(TestUsers.OwnerId, run.Id, batchSize: 2);
        Assert.Equal("a:50", second.Cursor);
        Assert.Equal(nameof(CatalogBackfillStatus.Running), second.Status);

        var third = await sut.RunBatchAsync(TestUsers.OwnerId, run.Id, batchSize: 2);
        Assert.Equal("a:150", third.Cursor);
        Assert.Equal(nameof(CatalogBackfillStatus.Completed), third.Status);

        Assert.Equal([100L, 200L, 300L], fetcher.Releases);
        Assert.Equal([50L, 150L], fetcher.Artists);
        Assert.Equal(5, third.Processed);
    }

    [Fact]
    public async Task CatalogResources_ReadsALegacyNumericCursorAsTheReleasePhase()
    {
        await using var db = NewDb(Guid.NewGuid().ToString("N"));
        var stale = DateTime.UtcNow.AddDays(-1);
        foreach (var id in new long[] { 100, 200, 300 })
            db.CatalogReleases.Add(new CatalogRelease { DiscogsId = id, FetchState = CatalogFetchState.Fetched, RefreshAfterUtc = stale, Title = $"r{id}" });
        db.CatalogArtists.Add(new CatalogArtist { DiscogsId = 5, FetchState = CatalogFetchState.Fetched, RefreshAfterUtc = stale, Name = "a5" });
        db.CatalogBackfillRuns.Add(new CatalogBackfillRun
        {
            OwnerUserId = TestUsers.OwnerId,
            Kind = CatalogBackfillKind.CatalogResources,
            Status = CatalogBackfillStatus.Running,
            Cursor = "200",
            StartedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var runId = (await db.CatalogBackfillRuns.IgnoreQueryFilters().SingleAsync()).Id;

        var fetcher = new RecordingFetcher();
        var sut = new CatalogBackfillService(db, new ClearingIdentity(db), fetcher, new SequenceImages(), new FakeIdentityDiscogsClient());
        var done = await sut.RunBatchAsync(TestUsers.OwnerId, runId, batchSize: 10);

        Assert.Equal([300L], fetcher.Releases);
        Assert.Equal([5L], fetcher.Artists);
        Assert.Equal("a:5", done.Cursor);
        Assert.Equal(nameof(CatalogBackfillStatus.Completed), done.Status);
    }

    [Fact]
    public async Task Images_CompletesOnlyWhenNothingWasAttempted()
    {
        await using var db = NewDb(Guid.NewGuid().ToString("N"));
        var images = new SequenceImages(2, 0);
        var sut = new CatalogBackfillService(db, new ClearingIdentity(db), new RecordingFetcher(), images, new FakeIdentityDiscogsClient());
        var run = await sut.StartAsync(TestUsers.OwnerId, CatalogBackfillKind.Images, dryRun: false, "admin");

        var partial = await sut.RunBatchAsync(TestUsers.OwnerId, run.Id, batchSize: 5);
        Assert.Equal(nameof(CatalogBackfillStatus.Running), partial.Status);
        Assert.Equal(2, partial.Processed);

        var done = await sut.RunBatchAsync(TestUsers.OwnerId, run.Id, batchSize: 5);
        Assert.Equal(nameof(CatalogBackfillStatus.Completed), done.Status);
        Assert.Equal(2, done.Processed);
    }

    [Fact]
    public async Task Images_RateLimit_PausesTheRun()
    {
        await using var db = NewDb(Guid.NewGuid().ToString("N"));
        var images = new SequenceImages { Throw = new ProviderRateLimitedException(TimeSpan.FromSeconds(30)) };
        var sut = new CatalogBackfillService(db, new ClearingIdentity(db), new RecordingFetcher(), images, new FakeIdentityDiscogsClient());
        var run = await sut.StartAsync(TestUsers.OwnerId, CatalogBackfillKind.Images, dryRun: false, "admin");

        var paused = await sut.RunBatchAsync(TestUsers.OwnerId, run.Id, batchSize: 5);

        Assert.Equal(nameof(CatalogBackfillStatus.Paused), paused.Status);
        Assert.False(string.IsNullOrWhiteSpace(paused.LastError));
    }

    private static int nextRunId = 1000;

    private static int AddRun(MusicHoarderDbContext db, CatalogBackfillStatus status, string? lastError, DateTime updatedAtUtc)
    {
        var id = Interlocked.Increment(ref nextRunId);
        db.CatalogBackfillRuns.Add(new CatalogBackfillRun
        {
            Id = id,
            OwnerUserId = TestUsers.OwnerId,
            Kind = CatalogBackfillKind.LibraryAlbums,
            Status = status,
            LastError = lastError,
            StartedAtUtc = updatedAtUtc,
            UpdatedAtUtc = updatedAtUtc,
        });
        return id;
    }

    private static MusicHoarderDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>().UseInMemoryDatabase(name).Options);

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

    /// <summary>Stands in for an ingest failure path that clears the shared change tracker.</summary>
    private sealed class ClearingIdentity(MusicHoarderDbContext db) : ICatalogIdentityService
    {
        public string? ThrowFor { get; init; }

        public Task<CatalogIdentityView> ResolveAlbumAsync(
            Guid ownerId, string albumArtist, string album, int? runId, bool dryRun, CancellationToken ct = default)
        {
            db.ChangeTracker.Clear();
            if (album == ThrowFor)
                throw new InvalidOperationException("ingest failed");
            return Task.FromResult(new CatalogIdentityView("LibraryAlbum", album, album, null, []));
        }

        public Task<CatalogIdentityView> ResolveArtistAsync(
            Guid ownerId, string artistName, int? runId, bool dryRun, CancellationToken ct = default)
        {
            db.ChangeTracker.Clear();
            return Task.FromResult(new CatalogIdentityView("LibraryArtist", artistName, artistName, null, []));
        }

        public Task<CatalogIdentityView> GetArtistIdentityAsync(Guid ownerId, string artistName, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<CatalogIdentityView> ChooseAsync(
            Guid ownerId, CatalogIdentitySubject subject, string subjectDisplay, CatalogEntityKind target, long discogsId,
            string decidedBy, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> RejectAsync(Guid ownerId, int decisionId, string decidedBy, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CatalogIdentityDecisionDto>> ListReviewAsync(Guid ownerId, int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingFetcher : ICatalogFetcher
    {
        public List<long> Releases { get; } = [];
        public List<long> Artists { get; } = [];

        public Task<CatalogArtist?> EnsureArtistAsync(long discogsId, bool withDiscography, bool force = false, CancellationToken ct = default)
        {
            Artists.Add(discogsId);
            return Task.FromResult<CatalogArtist?>(null);
        }

        public Task<CatalogLabel?> EnsureLabelAsync(long discogsId, bool withReleases, bool force = false, CancellationToken ct = default) =>
            Task.FromResult<CatalogLabel?>(null);

        public Task<CatalogMaster?> EnsureMasterAsync(long discogsId, bool withVersions, bool force = false, CancellationToken ct = default) =>
            Task.FromResult<CatalogMaster?>(null);

        public Task<CatalogRelease?> EnsureReleaseAsync(long discogsId, bool force = false, CancellationToken ct = default)
        {
            Releases.Add(discogsId);
            return Task.FromResult<CatalogRelease?>(null);
        }
    }

    private sealed class SequenceImages(params int[] attempts) : ICatalogImageDownloader
    {
        private int _call;

        public Exception? Throw { get; init; }

        public Task<int> DownloadPendingAsync(int maxImages, CancellationToken ct = default)
        {
            if (Throw is not null)
                return Task.FromException<int>(Throw);
            return Task.FromResult(_call < attempts.Length ? attempts[_call++] : 0);
        }
    }

    private sealed class RecordingBackfill : ICatalogBackfillService
    {
        public List<(int RunId, int BatchSize)> Calls { get; } = [];

        public Task<CatalogBackfillRunDto> RunBatchAsync(Guid ownerId, int runId, int batchSize, CancellationToken ct = default)
        {
            Calls.Add((runId, batchSize));
            return Task.FromResult<CatalogBackfillRunDto>(null!);
        }

        public Task<CatalogBackfillRunDto> StartAsync(
            Guid ownerId, CatalogBackfillKind kind, bool dryRun, string startedBy, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<CatalogBackfillRunDto?> RollbackAsync(Guid ownerId, int runId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CatalogBackfillRunDto>> ListRunsAsync(Guid ownerId, int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
