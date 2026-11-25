using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Catalog.Identity;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Catalog.Identity;

public class CatalogIdentityServiceTests
{
    [Fact]
    public async Task ResolveArtist_MusicBrainzRelation_AutoAccepts()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        AddSong(db, owner, "Aphex Twin", "SAW", "Xtal", artistMbid: "mb-artist-1");
        await db.SaveChangesAsync();

        var mb = new FakeMusicBrainz();
        mb.ArtistRels["mb-artist-1"] =
            [new MusicBrainzUrlRelation("discogs", "https://www.discogs.com/artist/45")];
        var fetcher = new FakeCatalogFetcher();
        var sut = CreateSut(db, fetcher, mb, new FakeIdentityDiscogsClient { IsAuthenticated = false });

        var view = await sut.ResolveArtistAsync(owner, "Aphex Twin", runId: null, dryRun: false);

        Assert.NotNull(view.Accepted);
        Assert.Equal(45, view.Accepted!.DiscogsId);
        Assert.Equal(nameof(CatalogIdentityMethod.MusicBrainzRelation), view.Accepted.Method);
        Assert.Equal(nameof(CatalogIdentityStatus.AutoAccepted), view.Accepted.Status);
    }

    [Fact]
    public async Task ResolveAlbum_MusicBrainzReleaseRel_AutoAccepts_AndWritesSongIds()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        var song = AddSong(db, owner, "Aphex Twin", "Selected Ambient Works 85-92", "Xtal",
            releaseMbid: "mb-release-1", durationSeconds: 291);
        await db.SaveChangesAsync();

        var mb = new FakeMusicBrainz();
        mb.ReleaseRels["mb-release-1"] =
            [new MusicBrainzUrlRelation("discogs", "https://www.discogs.com/release/1545313")];
        var fetcher = new FakeCatalogFetcher();
        var sut = CreateSut(db, fetcher, mb, new FakeIdentityDiscogsClient { IsAuthenticated = false });

        var view = await sut.ResolveAlbumAsync(owner, "Aphex Twin", "Selected Ambient Works 85-92", null, dryRun: false);

        Assert.NotNull(view.Accepted);
        Assert.Equal(1545313, view.Accepted!.DiscogsId);
        Assert.Contains(1545313, fetcher.EnsuredReleases);

        await db.Entry(song).ReloadAsync();
        Assert.Equal(1545313, song.DiscogsReleaseId);
        Assert.Equal(45, song.DiscogsArtistId);
        Assert.NotEmpty(await db.SongMetadataChanges.Where(c => c.SongId == song.Id).ToListAsync());
    }

    [Fact]
    public async Task ResolveAlbum_DryRun_WritesNothingToSongs()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        var song = AddSong(db, owner, "Aphex Twin", "SAW", "Xtal", releaseMbid: "mb-r");
        await db.SaveChangesAsync();

        var mb = new FakeMusicBrainz();
        mb.ReleaseRels["mb-r"] =
            [new MusicBrainzUrlRelation("discogs", "https://www.discogs.com/release/99")];
        var sut = CreateSut(db, new FakeCatalogFetcher(), mb, new FakeIdentityDiscogsClient { IsAuthenticated = false });

        await sut.ResolveAlbumAsync(owner, "Aphex Twin", "SAW", null, dryRun: true);

        await db.Entry(song).ReloadAsync();
        Assert.Null(song.DiscogsReleaseId);
        Assert.Empty(await db.SongMetadataChanges.ToListAsync());
        Assert.All(await db.CatalogIdentityDecisions.ToListAsync(),
            d => Assert.Equal(CatalogIdentityStatus.Candidate, d.Status));
    }

    [Fact]
    public async Task ResolveAlbum_LowConfidence_RecordsCandidates()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        AddSong(db, owner, "Aphex Twin", "Mystery Album", "Track A", durationSeconds: 120);
        await db.SaveChangesAsync();

        var discogs = new FakeIdentityDiscogsClient { IsAuthenticated = true };
        var release = new DiscogsRelease(
            Id: 77,
            Title: "Mystery Album",
            Artists: [new DiscogsArtistRef(45, "Aphex Twin")],
            Tracklist: [new DiscogsTrack("1", "track", "Totally Different", "3:00")]);
        discogs.Releases[77] = new DiscogsDocument<DiscogsRelease>(release, "{}");
        discogs.SearchResults =
        [
            new DiscogsSearchResult(77, "release", "Aphex Twin - Mystery Album"),
        ];

        var opts = Microsoft.Extensions.Options.Options.Create(new DiscogsOptions { AutoAcceptConfidence = 0.99 });
        var sut = CreateSut(db, new FakeCatalogFetcher(), new FakeMusicBrainz(), discogs, opts);

        var view = await sut.ResolveAlbumAsync(owner, "Aphex Twin", "Mystery Album", null, dryRun: false);

        Assert.Null(view.Accepted);
        Assert.NotEmpty(view.Candidates);
        Assert.All(view.Candidates, c => Assert.Equal(nameof(CatalogIdentityStatus.Candidate), c.Status));
    }

    [Fact]
    public async Task Choose_SupersedesEarlierDecisions_AndApplies()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        var song = AddSong(db, owner, "Aphex Twin", "SAW", "Xtal");
        db.CatalogIdentityDecisions.Add(new CatalogIdentityDecision
        {
            OwnerUserId = owner,
            SubjectKind = CatalogIdentitySubject.LibraryAlbum,
            SubjectKey = DiscogsMatchScoring.AlbumSubjectKey("Aphex Twin", "SAW"),
            SubjectDisplay = "Aphex Twin – SAW",
            TargetKind = CatalogEntityKind.Release,
            DiscogsId = 1,
            Method = CatalogIdentityMethod.NameSearch,
            Confidence = 0.5,
            Status = CatalogIdentityStatus.Candidate,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var sut = CreateSut(db, new FakeCatalogFetcher(), new FakeMusicBrainz(), new FakeIdentityDiscogsClient());
        var view = await sut.ChooseAsync(
            owner, CatalogIdentitySubject.LibraryAlbum, "Aphex Twin – SAW",
            CatalogEntityKind.Release, 1545313, decidedBy: owner.ToString("D"));

        Assert.NotNull(view.Accepted);
        Assert.Equal(nameof(CatalogIdentityStatus.Chosen), view.Accepted!.Status);
        Assert.Equal(1545313, view.Accepted.DiscogsId);
        Assert.Contains(await db.CatalogIdentityDecisions.ToListAsync(),
            d => d.Status == CatalogIdentityStatus.Superseded && d.DiscogsId == 1);

        await db.Entry(song).ReloadAsync();
        Assert.Equal(1545313, song.DiscogsReleaseId);
    }

    [Fact]
    public async Task Reject_MarksCandidateRejected()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        var decision = new CatalogIdentityDecision
        {
            OwnerUserId = owner,
            SubjectKind = CatalogIdentitySubject.LibraryArtist,
            SubjectKey = "aphex twin",
            SubjectDisplay = "Aphex Twin",
            TargetKind = CatalogEntityKind.Artist,
            DiscogsId = 45,
            Method = CatalogIdentityMethod.NameSearch,
            Confidence = 0.6,
            Status = CatalogIdentityStatus.Candidate,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.CatalogIdentityDecisions.Add(decision);
        await db.SaveChangesAsync();

        var sut = CreateSut(db, new FakeCatalogFetcher(), new FakeMusicBrainz(), new FakeIdentityDiscogsClient());
        Assert.True(await sut.RejectAsync(owner, decision.Id, "tester"));
        await db.Entry(decision).ReloadAsync();
        Assert.Equal(CatalogIdentityStatus.Rejected, decision.Status);
    }

    private static CatalogIdentityService CreateSut(
        MusicHoarderDbContext db,
        FakeCatalogFetcher fetcher,
        FakeMusicBrainz mb,
        FakeIdentityDiscogsClient discogs,
        IOptions<DiscogsOptions>? options = null) =>
        new(db, discogs, fetcher, mb, options ?? Microsoft.Extensions.Options.Options.Create(new DiscogsOptions { AutoAcceptConfidence = 0.9 }));

    private static MusicHoarderDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private static SongMetadata AddSong(
        MusicHoarderDbContext db,
        Guid owner,
        string artist,
        string album,
        string title,
        string? artistMbid = null,
        string? releaseMbid = null,
        int? durationSeconds = null)
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
            DurationSeconds = durationSeconds,
            AlbumArtistMusicBrainzId = artistMbid,
            MusicBrainzReleaseId = releaseMbid,
            EnrichmentStatus = EnrichmentStatus.Matched,
        };
        db.Songs.Add(song);
        return song;
    }
}
