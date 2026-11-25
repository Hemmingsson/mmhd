using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Catalog.Identity;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Catalog.Identity;

public class CatalogIdentityRejectTests
{
    private const string AlbumDisplay = "Aphex Twin – SAW";
    private const long ReleaseId = 249504;
    // FakeCatalogFetcher: master = release + 500, main artist = 45.
    private const long MasterId = ReleaseId + 500;
    private const long ArtistId = 45;

    [Fact]
    public async Task Reject_ChosenAlbum_RestoresPreviousIds_AndMarksChangesReverted()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        var song = AddSong(db, owner, "Aphex Twin", "SAW", "Xtal");
        song.DiscogsReleaseId = 111;
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var decisionId = await ChooseAsync(sut, owner);
        await db.Entry(song).ReloadAsync();
        Assert.Equal(ReleaseId, song.DiscogsReleaseId);
        Assert.Equal(MasterId, song.DiscogsMasterId);
        Assert.Equal(ArtistId, song.DiscogsArtistId);

        Assert.True(await sut.RejectAsync(owner, decisionId, "tester"));

        await db.Entry(song).ReloadAsync();
        Assert.Equal(111, song.DiscogsReleaseId);
        Assert.Null(song.DiscogsMasterId);
        Assert.Null(song.DiscogsArtistId);
        var changes = await db.SongMetadataChanges.Where(c => c.SongId == song.Id).ToListAsync();
        Assert.Equal(3, changes.Count);
        Assert.All(changes, c => Assert.NotNull(c.RevertedAtUtc));
        Assert.Equal(CatalogIdentityStatus.Rejected, (await db.CatalogIdentityDecisions.FindAsync(decisionId))!.Status);
    }

    [Fact]
    public async Task Reject_AutoAcceptedAlbum_FromRun_RestoresIds()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        var song = AddSong(db, owner, "Aphex Twin", "SAW", "Xtal", releaseMbid: "mb-r");
        var run = new CatalogBackfillRun { OwnerUserId = owner, Kind = CatalogBackfillKind.LibraryAlbums, StartedAtUtc = DateTime.UtcNow };
        db.CatalogBackfillRuns.Add(run);
        await db.SaveChangesAsync();

        var mb = new FakeMusicBrainz();
        mb.ReleaseRels["mb-r"] = [new MusicBrainzUrlRelation("discogs", $"https://www.discogs.com/release/{ReleaseId}")];
        var sut = CreateSut(db, mb);
        var view = await sut.ResolveAlbumAsync(owner, "Aphex Twin", "SAW", run.Id, dryRun: false);
        Assert.Equal(nameof(CatalogIdentityStatus.AutoAccepted), view.Accepted!.Status);
        Assert.All(await db.SongMetadataChanges.ToListAsync(), c => Assert.Equal(run.ChangeSource, c.Source));

        Assert.True(await sut.RejectAsync(owner, view.Accepted.DecisionId, "tester"));

        await db.Entry(song).ReloadAsync();
        Assert.Null(song.DiscogsReleaseId);
        Assert.Null(song.DiscogsMasterId);
        Assert.Null(song.DiscogsArtistId);
        Assert.All(await db.SongMetadataChanges.ToListAsync(), c => Assert.NotNull(c.RevertedAtUtc));
    }

    [Fact]
    public async Task Reject_ChosenMaster_RestoresResolvedReleaseToo()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        var song = AddSong(db, owner, "Aphex Twin", "SAW", "Xtal");
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        await sut.ChooseAsync(owner, CatalogIdentitySubject.LibraryAlbum, AlbumDisplay,
            CatalogEntityKind.Master, 7000, decidedBy: "tester");
        var decision = await db.CatalogIdentityDecisions.SingleAsync(d => d.Status == CatalogIdentityStatus.Chosen);
        await db.Entry(song).ReloadAsync();
        Assert.Equal(8000, song.DiscogsReleaseId);
        Assert.Equal(7000, song.DiscogsMasterId);

        Assert.True(await sut.RejectAsync(owner, decision.Id, "tester"));

        await db.Entry(song).ReloadAsync();
        Assert.Null(song.DiscogsReleaseId);
        Assert.Null(song.DiscogsMasterId);
        Assert.Null(song.DiscogsArtistId);
    }

    [Fact]
    public async Task Reject_AfterUserEditedReleaseId_KeepsUserValue()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        var song = AddSong(db, owner, "Aphex Twin", "SAW", "Xtal");
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var decisionId = await ChooseAsync(sut, owner);
        await db.Entry(song).ReloadAsync();
        song.DiscogsReleaseId = 999;
        await db.SaveChangesAsync();

        Assert.True(await sut.RejectAsync(owner, decisionId, "tester"));

        await db.Entry(song).ReloadAsync();
        Assert.Equal(999, song.DiscogsReleaseId);
        var releaseChange = await db.SongMetadataChanges.SingleAsync(c =>
            c.SongId == song.Id && c.FieldName == nameof(SongMetadata.DiscogsReleaseId));
        Assert.Null(releaseChange.RevertedAtUtc);
        // Untouched sibling fields from the same acceptance are still reverted.
        Assert.Null(song.DiscogsMasterId);
        Assert.Null(song.DiscogsArtistId);
    }

    [Fact]
    public async Task Reject_Candidate_ChangesNoSong()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        var song = AddSong(db, owner, "Aphex Twin", "SAW", "Xtal");
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        // An accepted release is applied, then a different candidate appears and is rejected.
        await ChooseAsync(sut, owner);
        var candidate = new CatalogIdentityDecision
        {
            OwnerUserId = owner,
            SubjectKind = CatalogIdentitySubject.LibraryAlbum,
            SubjectKey = DiscogsMatchScoring.AlbumSubjectKey("Aphex Twin", "SAW"),
            SubjectDisplay = AlbumDisplay,
            TargetKind = CatalogEntityKind.Release,
            DiscogsId = ReleaseId,
            Method = CatalogIdentityMethod.NameSearch,
            Confidence = 0.5,
            Status = CatalogIdentityStatus.Candidate,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.CatalogIdentityDecisions.Add(candidate);
        await db.SaveChangesAsync();

        Assert.True(await sut.RejectAsync(owner, candidate.Id, "tester"));

        await db.Entry(song).ReloadAsync();
        Assert.Equal(ReleaseId, song.DiscogsReleaseId);
        Assert.Equal(MasterId, song.DiscogsMasterId);
        Assert.Equal(ArtistId, song.DiscogsArtistId);
        Assert.All(await db.SongMetadataChanges.ToListAsync(), c => Assert.Null(c.RevertedAtUtc));
        await db.Entry(candidate).ReloadAsync();
        Assert.Equal(CatalogIdentityStatus.Rejected, candidate.Status);
    }

    [Fact]
    public async Task Reject_LeavesOtherOwnersIdenticalAlbumUntouched()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        var other = TestUsers.FriendId;
        var mine = AddSong(db, owner, "Aphex Twin", "SAW", "Xtal");
        var theirs = AddSong(db, other, "Aphex Twin", "SAW", "Xtal");
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var myDecision = await ChooseAsync(sut, owner);
        await ChooseAsync(sut, other);

        Assert.True(await sut.RejectAsync(owner, myDecision, "tester"));

        await db.Entry(mine).ReloadAsync();
        await db.Entry(theirs).ReloadAsync();
        Assert.Null(mine.DiscogsReleaseId);
        Assert.Equal(ReleaseId, theirs.DiscogsReleaseId);
        Assert.Equal(MasterId, theirs.DiscogsMasterId);
        Assert.Equal(ArtistId, theirs.DiscogsArtistId);
        Assert.All(
            await db.SongMetadataChanges.Where(c => c.SongId == theirs.Id).ToListAsync(),
            c => Assert.Null(c.RevertedAtUtc));
        Assert.False(await sut.RejectAsync(other, myDecision, "intruder"));
    }

    [Fact]
    public async Task Reject_Twice_IsNoOp()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        var song = AddSong(db, owner, "Aphex Twin", "SAW", "Xtal");
        song.DiscogsReleaseId = 111;
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        var decisionId = await ChooseAsync(sut, owner);
        Assert.True(await sut.RejectAsync(owner, decisionId, "tester"));
        var decision = (await db.CatalogIdentityDecisions.FindAsync(decisionId))!;
        var decidedAt = decision.DecidedAtUtc;
        var revertedAt = (await db.SongMetadataChanges.ToListAsync()).Select(c => c.RevertedAtUtc).ToList();

        // User re-sets the id by hand; a second reject must not undo it again.
        await db.Entry(song).ReloadAsync();
        song.DiscogsReleaseId = ReleaseId;
        await db.SaveChangesAsync();

        Assert.True(await sut.RejectAsync(owner, decisionId, "tester"));

        await db.Entry(song).ReloadAsync();
        await db.Entry(decision).ReloadAsync();
        Assert.Equal(ReleaseId, song.DiscogsReleaseId);
        Assert.Equal(CatalogIdentityStatus.Rejected, decision.Status);
        Assert.Equal(decidedAt, decision.DecidedAtUtc);
        Assert.Equal(revertedAt, (await db.SongMetadataChanges.ToListAsync()).Select(c => c.RevertedAtUtc).ToList());
    }

    private static async Task<int> ChooseAsync(CatalogIdentityService sut, Guid owner)
    {
        var view = await sut.ChooseAsync(owner, CatalogIdentitySubject.LibraryAlbum, AlbumDisplay,
            CatalogEntityKind.Release, ReleaseId, decidedBy: owner.ToString("D"));
        return view.Accepted!.DecisionId;
    }

    private static CatalogIdentityService CreateSut(MusicHoarderDbContext db, FakeMusicBrainz? mb = null) =>
        new(db, new FakeIdentityDiscogsClient { IsAuthenticated = false }, new FakeCatalogFetcher(), mb ?? new FakeMusicBrainz(),
            Microsoft.Extensions.Options.Options.Create(new DiscogsOptions { AutoAcceptConfidence = 0.9 }));

    private static MusicHoarderDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static SongMetadata AddSong(
        MusicHoarderDbContext db, Guid owner, string artist, string album, string title, string? releaseMbid = null)
    {
        var song = new SongMetadata
        {
            OwnerUserId = owner,
            SourcePath = $"/src/{owner:N}/{album}/{title}.flac",
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
}
