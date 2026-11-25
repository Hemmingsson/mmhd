using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Catalog.Query;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Catalog.Query;

public class CatalogQueryServiceTests
{
    [Fact]
    public async Task ListReleases_IncludesDiscogsOnlyRelease_WithNoOwnedSongs()
    {
        await using var db = CatalogTestKit.NewContext();
        var graph = CatalogTestKit.SeedDiscogsOnlyRelease(db);
        var sut = new CatalogQueryService(db);

        var page = await sut.ListReleasesAsync(new CatalogReleaseQuery());

        var card = Assert.Single(page.Items);
        Assert.Equal(graph.Release.Id, card.Id);
        Assert.False(card.Owned);
        Assert.Contains("Dub Techno", card.Styles);
    }

    [Fact]
    public async Task GetRelease_ReturnsFullDetail_ForDiscogsOnlyGraph()
    {
        await using var db = CatalogTestKit.NewContext();
        var graph = CatalogTestKit.SeedDiscogsOnlyRelease(db);
        var sut = new CatalogQueryService(db);

        var detail = await sut.GetReleaseAsync(graph.Release.Id);

        Assert.NotNull(detail);
        Assert.Equal("Lyot Rmx", detail.Title);
        Assert.Equal(2, detail.Tracklist.Count);
        Assert.Contains(detail.Artists, a => a.Artist.DiscogsId == graph.Artist.DiscogsId);
        Assert.Contains(detail.ExtraArtists, a => a.Role == "Producer");
        Assert.Equal("Remix", Assert.Single(detail.Tracklist[0].Credits).Role);
        Assert.Contains(detail.Labels, l => l.CatalogNumber == "BC-02");
        Assert.Contains(detail.Formats, f => f.Name == "Vinyl" && f.Descriptions.Count == 2);
        Assert.Contains(detail.Identifiers, i => i.Type == "Barcode");
        Assert.Contains(detail.Urls, u => u.Kind == "purchase" && u.Url.Contains("bandcamp", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(detail.Urls, u => u.Kind == "streaming" && u.Url.Contains("spotify", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(detail.Urls, u => u.Url.Contains("/sell/release/4001", StringComparison.Ordinal));
        Assert.Equal(0, detail.Ownership.OwnedSongs);
        Assert.All(detail.Tracklist, t => Assert.False(t.Owned));
    }

    [Fact]
    public async Task ListReleases_FiltersByQ_Style_Country_Year_AndPages()
    {
        await using var db = CatalogTestKit.NewContext();
        var graph = CatalogTestKit.SeedDiscogsOnlyRelease(db);
        db.CatalogReleases.Add(new CatalogRelease
        {
            DiscogsId = 4002,
            Title = "Other",
            TitleKey = CatalogKeys.TitleKey("Other"),
            Year = 2001,
            Country = "US",
            FetchState = CatalogFetchState.Listed,
            CreatedAtUtc = DateTime.UtcNow,
        });
        db.SaveChanges();
        var sut = new CatalogQueryService(db);

        var byQ = await sut.ListReleasesAsync(new CatalogReleaseQuery(Q: "lyot"));
        Assert.Single(byQ.Items);

        var byStyle = await sut.ListReleasesAsync(new CatalogReleaseQuery(Style: "Dub Techno"));
        Assert.Single(byStyle.Items);

        var byCountry = await sut.ListReleasesAsync(new CatalogReleaseQuery(Country: "Germany"));
        Assert.Single(byCountry.Items);

        var byYear = await sut.ListReleasesAsync(new CatalogReleaseQuery(YearFrom: 1990, YearTo: 1995));
        Assert.Single(byYear.Items);

        var page1 = await sut.ListReleasesAsync(new CatalogReleaseQuery(Page: 1, PageSize: 1, Sort: "title"));
        Assert.Single(page1.Items);
        Assert.Equal(2, page1.Total);
        Assert.Equal(1, page1.PageSize);
    }

    [Fact]
    public async Task OwnedFlags_RespectOwnerIsolation()
    {
        var options = CatalogTestKit.SharedOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            var graph = CatalogTestKit.SeedDiscogsOnlyRelease(seed);
            CatalogTestKit.AddOwnedSong(seed, CatalogTestKit.OwnerA, graph.Release, "Lyot (Reduce)",
                artistDiscogsId: graph.Artist.DiscogsId, masterDiscogsId: graph.Master.DiscogsId);
            CatalogTestKit.AddOwnedSong(seed, CatalogTestKit.OwnerB, graph.Release, "Lyot (Exduce)",
                artistDiscogsId: graph.Artist.DiscogsId, masterDiscogsId: graph.Master.DiscogsId);
        }

        await using var asA = CatalogTestKit.ContextOn(options, CatalogTestKit.OwnerA);
        var pageA = await new CatalogQueryService(asA).ListReleasesAsync(new CatalogReleaseQuery());
        var cardA = Assert.Single(pageA.Items);
        Assert.True(cardA.Owned);

        var detailA = await new CatalogQueryService(asA).GetReleaseAsync(cardA.Id);
        Assert.NotNull(detailA);
        Assert.Equal(1, detailA.Ownership.OwnedSongs);
        Assert.True(detailA.Tracklist.Single(t => t.Title.Contains("Reduce")).Owned);
        Assert.False(detailA.Tracklist.Single(t => t.Title.Contains("Exduce")).Owned);

        await using var asB = CatalogTestKit.ContextOn(options, CatalogTestKit.OwnerB);
        var detailB = await new CatalogQueryService(asB).GetReleaseAsync(cardA.Id);
        Assert.NotNull(detailB);
        Assert.Equal(1, detailB.Ownership.OwnedSongs);
        Assert.False(detailB.Tracklist.Single(t => t.Title.Contains("Reduce")).Owned);
        Assert.True(detailB.Tracklist.Single(t => t.Title.Contains("Exduce")).Owned);
    }

    [Fact]
    public async Task ListReleases_OwnedFilter_UsesAmbientOwner()
    {
        var options = CatalogTestKit.SharedOptions();
        int releaseId;
        await using (var seed = new MusicHoarderDbContext(options))
        {
            var graph = CatalogTestKit.SeedDiscogsOnlyRelease(seed);
            releaseId = graph.Release.Id;
            CatalogTestKit.AddOwnedSong(seed, CatalogTestKit.OwnerA, graph.Release, "Lyot (Reduce)",
                artistDiscogsId: graph.Artist.DiscogsId);
        }

        await using var asA = CatalogTestKit.ContextOn(options, CatalogTestKit.OwnerA);
        var owned = await new CatalogQueryService(asA).ListReleasesAsync(new CatalogReleaseQuery(Owned: true));
        Assert.Single(owned.Items);

        await using var asB = CatalogTestKit.ContextOn(options, CatalogTestKit.OwnerB);
        var ownedB = await new CatalogQueryService(asB).ListReleasesAsync(new CatalogReleaseQuery(Owned: true));
        Assert.Empty(ownedB.Items);
        var unownedB = await new CatalogQueryService(asB).ListReleasesAsync(new CatalogReleaseQuery(Owned: false));
        Assert.Contains(unownedB.Items, i => i.Id == releaseId);
    }

    [Fact]
    public async Task FindArtistForLibraryName_UsesMostCommonDiscogsArtistId()
    {
        await using var db = CatalogTestKit.NewContext(CatalogTestKit.OwnerA);
        var graph = CatalogTestKit.SeedDiscogsOnlyRelease(db);
        CatalogTestKit.AddOwnedSong(db, CatalogTestKit.OwnerA, graph.Release, "A", graph.Artist.DiscogsId);
        CatalogTestKit.AddOwnedSong(db, CatalogTestKit.OwnerA, graph.Release, "B", graph.Artist.DiscogsId);

        var id = await new CatalogQueryService(db).FindArtistForLibraryNameAsync("Basic Channel");
        Assert.Equal(graph.Artist.Id, id);
    }

    [Fact]
    public async Task FindArtistForLibraryName_FallsBackToIdentityDecision()
    {
        await using var db = CatalogTestKit.NewContext(CatalogTestKit.OwnerA);
        var graph = CatalogTestKit.SeedDiscogsOnlyRelease(db);
        db.CatalogIdentityDecisions.Add(new CatalogIdentityDecision
        {
            OwnerUserId = CatalogTestKit.OwnerA,
            SubjectKind = CatalogIdentitySubject.LibraryArtist,
            SubjectKey = CatalogKeys.NameKey("Library Name"),
            SubjectDisplay = "Library Name",
            TargetKind = CatalogEntityKind.Artist,
            DiscogsId = graph.Artist.DiscogsId,
            Method = CatalogIdentityMethod.Manual,
            Confidence = 1,
            Status = CatalogIdentityStatus.Chosen,
            CreatedAtUtc = DateTime.UtcNow,
            DecidedAtUtc = DateTime.UtcNow,
            DecidedBy = "test",
        });
        db.SaveChanges();

        var id = await new CatalogQueryService(db).FindArtistForLibraryNameAsync("Library Name");
        Assert.Equal(graph.Artist.Id, id);
    }

    [Fact]
    public async Task GetFacets_And_ListStyles_ReturnCounts()
    {
        await using var db = CatalogTestKit.NewContext();
        CatalogTestKit.SeedDiscogsOnlyRelease(db);
        var sut = new CatalogQueryService(db);

        var facets = await sut.GetFacetsAsync();
        Assert.Contains(facets.Styles, s => s.Name == "Dub Techno" && s.ReleaseCount >= 1);
        Assert.Contains(facets.Formats, f => f.Value == "Vinyl");
        Assert.Contains(facets.Countries, c => c.Value == "Germany");
        Assert.Equal(1993, facets.MinYear);
        Assert.Equal(1993, facets.MaxYear);

        var styles = await sut.ListStylesAsync(new CatalogListQuery());
        Assert.Contains(styles.Items, s => s.Name == "Dub Techno");
    }

    [Fact]
    public async Task GetArtist_IncludesDiscographyAndLibraryNames()
    {
        await using var db = CatalogTestKit.NewContext(CatalogTestKit.OwnerA);
        var graph = CatalogTestKit.SeedDiscogsOnlyRelease(db);
        CatalogTestKit.AddOwnedSong(db, CatalogTestKit.OwnerA, graph.Release, "Lyot (Reduce)", graph.Artist.DiscogsId);

        var detail = await new CatalogQueryService(db).GetArtistAsync(graph.Artist.Id);
        Assert.NotNull(detail);
        Assert.NotEmpty(detail.Discography);
        Assert.Contains("Basic Channel", detail.LibraryArtistNames);
        Assert.True(detail.Discography[0].Owned);
    }
}
