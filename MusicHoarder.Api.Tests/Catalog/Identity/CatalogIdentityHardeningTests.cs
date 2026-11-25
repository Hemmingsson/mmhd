using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Catalog.Identity;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Catalog.Identity;

public class CatalogIdentityHardeningTests
{
    private const string Barcode = "5021603064220";

    // --- search-hit auto-accept (barcode / label+catno) ---

    [Fact]
    public async Task ResolveAlbum_BarcodeHit_WithDifferentTitleAndArtist_IsNotAutoAccepted()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        var song = AddSong(db, owner, "Aphex Twin", "SAW", "Xtal", upc: Barcode);
        await db.SaveChangesAsync();

        var discogs = BarcodeOnly(new DiscogsSearchResult(500, "release", "Someone Else - Other Album", Barcode: [Barcode]));
        var sut = CreateSut(db, new FakeMusicBrainz(), discogs);

        var view = await sut.ResolveAlbumAsync(owner, "Aphex Twin", "SAW", null, dryRun: false);

        Assert.Null(view.Accepted);
        Assert.DoesNotContain(await db.CatalogIdentityDecisions.ToListAsync(), d => d.DiscogsId == 500);
        await db.Entry(song).ReloadAsync();
        Assert.Null(song.DiscogsReleaseId);
    }

    [Fact]
    public async Task ResolveAlbum_BarcodeHit_WithSameTitleButDifferentArtist_IsNotAutoAccepted()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        AddSong(db, owner, "Aphex Twin", "SAW", "Xtal", upc: Barcode);
        await db.SaveChangesAsync();

        var discogs = BarcodeOnly(new DiscogsSearchResult(501, "release", "Tribute Band - SAW", Barcode: [Barcode]));
        var sut = CreateSut(db, new FakeMusicBrainz(), discogs);

        var view = await sut.ResolveAlbumAsync(owner, "Aphex Twin", "SAW", null, dryRun: false);

        Assert.Null(view.Accepted);
        Assert.DoesNotContain(await db.CatalogIdentityDecisions.ToListAsync(),
            d => d.DiscogsId == 501 && d.Status == CatalogIdentityStatus.AutoAccepted);
    }

    [Fact]
    public async Task ResolveAlbum_CatnoHit_WithDifferentTitle_IsNotAutoAccepted()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        AddSong(db, owner, "Aphex Twin", "SAW", "Xtal", label: "Apollo", catno: "AMB 3922");
        await db.SaveChangesAsync();

        var discogs = new FakeIdentityDiscogsClient
        {
            SearchHandler = q => Page(q.Catno is not null
                ? [new DiscogsSearchResult(502, "release", "Aphex Twin - Classics", Catno: "AMB 3922")]
                : []),
        };
        var sut = CreateSut(db, new FakeMusicBrainz(), discogs);

        var view = await sut.ResolveAlbumAsync(owner, "Aphex Twin", "SAW", null, dryRun: false);

        Assert.Null(view.Accepted);
        Assert.DoesNotContain(await db.CatalogIdentityDecisions.ToListAsync(), d => d.DiscogsId == 502);
    }

    [Fact]
    public async Task ResolveAlbum_BarcodeHit_Matching_StillAutoAccepts()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        var song = AddSong(db, owner, "Aphex Twin", "SAW", "Xtal", upc: Barcode);
        await db.SaveChangesAsync();

        var discogs = BarcodeOnly(new DiscogsSearchResult(503, "release", "Aphex Twin (2) - SAW", Barcode: [Barcode]));
        var sut = CreateSut(db, new FakeMusicBrainz(), discogs);

        var view = await sut.ResolveAlbumAsync(owner, "Aphex Twin", "SAW", null, dryRun: false);

        Assert.NotNull(view.Accepted);
        Assert.Equal(503, view.Accepted!.DiscogsId);
        Assert.Equal(nameof(CatalogIdentityMethod.Barcode), view.Accepted.Method);
        Assert.True(view.Accepted.Confidence >= 0.95);
        await db.Entry(song).ReloadAsync();
        Assert.Equal(503, song.DiscogsReleaseId);
    }

    [Fact]
    public async Task ResolveAlbum_CompilationBarcodeHit_DiscogsVarious_AutoAccepts()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        AddSong(db, owner, "Various Artists", "Warp 10+1", "Track", upc: Barcode, artist: "Autechre");
        await db.SaveChangesAsync();

        var discogs = BarcodeOnly(new DiscogsSearchResult(504, "release", "Various - Warp 10+1", Barcode: [Barcode]));
        var sut = CreateSut(db, new FakeMusicBrainz(), discogs);

        var view = await sut.ResolveAlbumAsync(owner, "Various Artists", "Warp 10+1", null, dryRun: false);

        Assert.NotNull(view.Accepted);
        Assert.Equal(504, view.Accepted!.DiscogsId);
    }

    [Theory]
    [InlineData("Aphex Twin - SAW", "Aphex Twin", true)]
    [InlineData("Aphex Twin (2) - SAW", "Aphex Twin", true)]
    [InlineData("Someone Else - SAW", "Aphex Twin", false)]
    [InlineData("SAW", "Aphex Twin", false)]
    [InlineData("Various - Warp 10+1", "Various Artists", true)]
    [InlineData("Various - Warp 10+1", "Aphex Twin", false)]
    public void SearchHitArtistMatches_ComparesArtistPartOfTitle(string title, string albumArtist, bool expected)
    {
        var hint = new DiscogsMatchScoring.LibraryAlbumHint(albumArtist, "SAW", null, null, null, null, []);
        Assert.Equal(expected, DiscogsMatchScoring.SearchHitArtistMatches(hint, new DiscogsSearchResult(1, "release", title)));
    }

    // --- artist MusicBrainz relations ---

    [Fact]
    public async Task ResolveArtist_CompilationAlbumArtistMbid_IsIgnored()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        AddSong(db, owner, "Various Artists", "Warp 10+1", "Track", artist: "Aphex Twin", albumArtistMbid: "mb-va");
        await db.SaveChangesAsync();

        var mb = new FakeMusicBrainz();
        mb.ArtistRels["mb-va"] = [new MusicBrainzUrlRelation("discogs", "https://www.discogs.com/artist/194")];
        var sut = CreateSut(db, mb, new FakeIdentityDiscogsClient { IsAuthenticated = false });

        var view = await sut.ResolveArtistAsync(owner, "Aphex Twin", runId: null, dryRun: false);

        Assert.Null(view.Accepted);
        Assert.DoesNotContain(await db.CatalogIdentityDecisions.ToListAsync(), d => d.DiscogsId == 194);
    }

    [Fact]
    public async Task ResolveArtist_FeaturedArtistMbid_IsIgnored_AlignedEntryWins()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        AddSong(db, owner, "Guest", "Collab", "Track", artist: "Aphex Twin",
            artists: "Guest; Aphex Twin", artistMbids: "mb-guest; mb-aphex");
        await db.SaveChangesAsync();

        var mb = new FakeMusicBrainz();
        mb.ArtistRels["mb-guest"] = [new MusicBrainzUrlRelation("discogs", "https://www.discogs.com/artist/999")];
        mb.ArtistRels["mb-aphex"] = [new MusicBrainzUrlRelation("discogs", "https://www.discogs.com/artist/45")];
        var sut = CreateSut(db, mb, new FakeIdentityDiscogsClient { IsAuthenticated = false });

        var view = await sut.ResolveArtistAsync(owner, "Aphex Twin", runId: null, dryRun: false);

        Assert.NotNull(view.Accepted);
        Assert.Equal(45, view.Accepted!.DiscogsId);
        Assert.Equal(nameof(CatalogIdentityMethod.MusicBrainzRelation), view.Accepted.Method);
        Assert.DoesNotContain(await db.CatalogIdentityDecisions.ToListAsync(), d => d.DiscogsId == 999);
    }

    [Fact]
    public async Task ResolveArtist_MisalignedArtistMbids_AreIgnored()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        AddSong(db, owner, "Aphex Twin", "Collab", "Track",
            artists: "Aphex Twin; Guest", artistMbids: "mb-guest");
        await db.SaveChangesAsync();

        var mb = new FakeMusicBrainz();
        mb.ArtistRels["mb-guest"] = [new MusicBrainzUrlRelation("discogs", "https://www.discogs.com/artist/999")];
        var sut = CreateSut(db, mb, new FakeIdentityDiscogsClient { IsAuthenticated = false });

        var view = await sut.ResolveArtistAsync(owner, "Aphex Twin", runId: null, dryRun: false);

        Assert.Null(view.Accepted);
        Assert.Empty(await db.CatalogIdentityDecisions.ToListAsync());
    }

    [Fact]
    public async Task ResolveArtist_DisagreeingMbids_RecordsCandidates_NoAutoAccept()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        AddSong(db, owner, "Nirvana", "Nevermind", "Lithium", albumArtistMbid: "mb-nirvana-us");
        AddSong(db, owner, "Nirvana", "Local Anaesthetic", "Rainbow Chaser", albumArtistMbid: "mb-nirvana-uk");
        await db.SaveChangesAsync();

        var mb = new FakeMusicBrainz();
        mb.ArtistRels["mb-nirvana-us"] = [new MusicBrainzUrlRelation("discogs", "https://www.discogs.com/artist/125246")];
        mb.ArtistRels["mb-nirvana-uk"] = [new MusicBrainzUrlRelation("discogs", "https://www.discogs.com/artist/307513")];
        var sut = CreateSut(db, mb, new FakeIdentityDiscogsClient { IsAuthenticated = false });

        var view = await sut.ResolveArtistAsync(owner, "Nirvana", runId: null, dryRun: false);

        Assert.Null(view.Accepted);
        Assert.Equal([125246L, 307513L], view.Candidates.Select(c => c.DiscogsId).Order().ToArray());
        Assert.All(view.Candidates, c =>
        {
            Assert.Equal(nameof(CatalogIdentityStatus.Candidate), c.Status);
            Assert.Equal(nameof(CatalogIdentityMethod.MusicBrainzRelation), c.Method);
            Assert.True(c.Confidence < 0.9);
        });
    }

    [Fact]
    public async Task ResolveArtist_AgreeingMbids_AutoAccepts()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        AddSong(db, owner, "Aphex Twin", "SAW", "Xtal", albumArtistMbid: "mb-a");
        AddSong(db, owner, "Aphex Twin", "Drukqs", "Avril 14th", albumArtistMbid: "mb-a-dup");
        await db.SaveChangesAsync();

        var mb = new FakeMusicBrainz();
        mb.ArtistRels["mb-a"] = [new MusicBrainzUrlRelation("discogs", "https://www.discogs.com/artist/45")];
        mb.ArtistRels["mb-a-dup"] = [new MusicBrainzUrlRelation("discogs", "https://www.discogs.com/artist/45-Aphex-Twin")];
        var sut = CreateSut(db, mb, new FakeIdentityDiscogsClient { IsAuthenticated = false });

        var view = await sut.ResolveArtistAsync(owner, "Aphex Twin", runId: null, dryRun: false);

        Assert.NotNull(view.Accepted);
        Assert.Equal(45, view.Accepted!.DiscogsId);
    }

    // --- album song loading ---

    [Fact]
    public async Task ResolveAlbum_TracksOnlyTheAlbumsSongs()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        var a = AddSong(db, owner, "Björk", "Homogenic", "Hunter", releaseMbid: "mb-r");
        var b = AddSong(db, owner, "Bjork", "HOMOGENIC!", "Joga");
        AddSong(db, owner, "Björk", "Post", "Army of Me");
        AddSong(db, owner, "Aphex Twin", "SAW", "Xtal");
        var deleted = AddSong(db, owner, "Björk", "Homogenic", "Bachelorette");
        deleted.DeletedAtUtc = DateTime.UtcNow;
        var theirs = AddSong(db, TestUsers.FriendId, "Björk", "Homogenic", "Hunter");
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var mb = new FakeMusicBrainz();
        mb.ReleaseRels["mb-r"] = [new MusicBrainzUrlRelation("discogs", "https://www.discogs.com/release/77")];
        var sut = CreateSut(db, mb, new FakeIdentityDiscogsClient { IsAuthenticated = false });

        var view = await sut.ResolveAlbumAsync(owner, "Bjork", "Homogenic", null, dryRun: false);

        Assert.Equal(77, view.Accepted!.DiscogsId);
        Assert.Equal([a.Id, b.Id], db.ChangeTracker.Entries<SongMetadata>().Select(e => e.Entity.Id).Order().ToArray());

        db.ChangeTracker.Clear();
        var songs = await db.Songs.AsNoTracking().ToDictionaryAsync(s => s.Id);
        Assert.Equal(77, songs[a.Id].DiscogsReleaseId);
        Assert.Equal(77, songs[b.Id].DiscogsReleaseId);
        Assert.Null(songs[deleted.Id].DiscogsReleaseId);
        Assert.Null(songs[theirs.Id].DiscogsReleaseId);
    }

    [Fact]
    public async Task Reject_TracksOnlyTheAlbumsSongs()
    {
        await using var db = NewDb();
        var owner = TestUsers.OwnerId;
        var song = AddSong(db, owner, "Aphex Twin", "SAW", "Xtal");
        AddSong(db, owner, "Aphex Twin", "Drukqs", "Avril 14th");
        AddSong(db, owner, "Autechre", "Amber", "Foil");
        await db.SaveChangesAsync();
        var sut = CreateSut(db, new FakeMusicBrainz(), new FakeIdentityDiscogsClient { IsAuthenticated = false });

        var view = await sut.ChooseAsync(owner, CatalogIdentitySubject.LibraryAlbum, "Aphex Twin – SAW",
            CatalogEntityKind.Release, 249504, decidedBy: "tester");
        db.ChangeTracker.Clear();

        Assert.True(await sut.RejectAsync(owner, view.Accepted!.DecisionId, "tester"));

        Assert.Equal([song.Id], db.ChangeTracker.Entries<SongMetadata>().Select(e => e.Entity.Id).ToArray());
        db.ChangeTracker.Clear();
        Assert.Null((await db.Songs.SingleAsync(s => s.Id == song.Id)).DiscogsReleaseId);
    }

    // --- helpers ---

    private static FakeIdentityDiscogsClient BarcodeOnly(DiscogsSearchResult hit) => new()
    {
        SearchHandler = q => Page(q.Barcode is not null ? [hit] : []),
    };

    private static DiscogsPage<DiscogsSearchResult> Page(List<DiscogsSearchResult> items) =>
        new(new DiscogsPagination(1, 1, 25, items.Count), items);

    private static CatalogIdentityService CreateSut(MusicHoarderDbContext db, FakeMusicBrainz mb, FakeIdentityDiscogsClient discogs) =>
        new(db, discogs, new FakeCatalogFetcher(), mb,
            Microsoft.Extensions.Options.Options.Create(new DiscogsOptions { AutoAcceptConfidence = 0.9 }));

    private static MusicHoarderDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static SongMetadata AddSong(
        MusicHoarderDbContext db,
        Guid owner,
        string albumArtist,
        string album,
        string title,
        string? artist = null,
        string? artists = null,
        string? artistMbids = null,
        string? albumArtistMbid = null,
        string? releaseMbid = null,
        string? upc = null,
        string? label = null,
        string? catno = null)
    {
        var song = new SongMetadata
        {
            OwnerUserId = owner,
            SourcePath = $"/src/{owner:N}/{albumArtist}/{album}/{title}.flac",
            FileName = $"{title}.flac",
            Extension = ".flac",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Title = title,
            Artist = artist ?? albumArtist,
            AlbumArtist = albumArtist,
            Album = album,
            Artists = artists,
            ArtistMusicBrainzIds = artistMbids,
            AlbumArtistMusicBrainzId = albumArtistMbid,
            MusicBrainzReleaseId = releaseMbid,
            Upc = upc,
            Label = label,
            CatalogNumber = catno,
            EnrichmentStatus = EnrichmentStatus.Matched,
        };
        db.Songs.Add(song);
        return song;
    }
}
