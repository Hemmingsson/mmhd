using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Catalog.Query;
using MusicHoarder.Api.Catalog.Ingestion;
using MusicHoarder.Api.Tests.Catalog.Ingestion;
using MusicHoarder.Api.Tests.Catalog.Identity;
using MusicHoarder.Api.Discogs;

namespace MusicHoarder.Api.Tests.Catalog;

public class LibraryProfileTests
{
    [Fact]
    public async Task LabelUsesConfirmedLocalIdentityWhenSearchIsUnavailable()
    {
        await using var db = CatalogTestKit.NewContext();
        var graph = CatalogTestKit.SeedDiscogsOnlyRelease(db);
        graph.Label.Profile = "Local biography";
        await db.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var result = await LibraryProfiles.GetAsync("label", graph.Label.Name, db,
            new CatalogQueryService(db), new CatalogIngestor(db, TimeProvider.System,
                CatalogIngestionTestKit.Monitor(CatalogIngestionTestKit.Options())), new FakeDiscogsClient { IsEnabled = false },
            new FakeMusicBrainz(), cache, default);

        var profile = Assert.IsType<LibraryProfile>(((IValueHttpResult)result).Value);
        Assert.Equal(graph.Label.DiscogsId, profile.DiscogsId);
        Assert.Equal("Local biography", profile.Profile);
        Assert.Null(profile.Image);
    }

    [Fact]
    public async Task OpeningALabelMakesItsDiscogsLogoAvailableInTheIndex()
    {
        await using var db = CatalogTestKit.NewContext();
        var graph = CatalogTestKit.SeedDiscogsOnlyRelease(db);
        const string logo = "https://example.org/logo.jpg";
        var label = new DiscogsLabel(graph.Label.DiscogsId, graph.Label.Name, Profile: "Based in Sweden.",
            Images: [new DiscogsImage("primary", logo, null, null, 128, 128)]);
        var client = new FakeDiscogsClient();
        client.Labels[label.Id] = new(label, System.Text.Json.JsonSerializer.Serialize(label, DiscogsJson.Options));
        var catalog = new CatalogQueryService(db);
        var ingestor = new CatalogIngestor(db, TimeProvider.System,
            CatalogIngestionTestKit.Monitor(CatalogIngestionTestKit.Options()));
        using var cache = new MemoryCache(new MemoryCacheOptions());

        await LibraryProfiles.GetAsync("label", label.Name, db, catalog, ingestor, client, new FakeMusicBrainz(), cache, default);
        var index = await catalog.ListLabelsAsync(new CatalogListQuery(Q: label.Name));
        Assert.Equal(logo, Assert.Single(index.Items).ThumbnailUrl);
    }

    [Theory]
    [InlineData("Swedish independent label founded by [a=Example Artist] and based in Malmö, in the southern-most part of Sweden.", "SE")]
    [InlineData("Independent record label based in Germany.", "DE")]
    [InlineData("Based in Paris, France.", "FR")]
    [InlineData("Based in the United States.", "US")]
    [InlineData("Based in Malmö.", null)]
    [InlineData("The founder toured in Sweden. Distributed in France.", null)]
    [InlineData("A label working with an artist based in Sweden.", null)]
    [InlineData("Label based in France. Based in Germany.", null)]
    public void CountryRequiresExplicitLabelLocation(string profile, string? expected) =>
        Assert.Equal(expected, LibraryProfiles.CountryFromProfile(profile));

    [Fact]
    public void SearchUsesOneCompleteExactMatch()
    {
        var page = new DiscogsPage<DiscogsSearchResult>(new(1, 1, 100, 2),
            [new(5, "label", "Test Recordings"), new(6, "label", "Test")]);
        Assert.Equal(6, LibraryProfiles.UniqueExactId(page, "label", "test"));
        Assert.Null(LibraryProfiles.UniqueExactId(page with { Pagination = new(1, 2, 100, 120) }, "label", "Test"));
    }

    [Fact]
    public void SimilarNamesAndNumberedHomonymsCannotEstablishIdentity()
    {
        var page = new DiscogsPage<DiscogsSearchResult>(new(1, 1, 100, 2),
            [new(7, "artist", "Origin"), new(8, "artist", "Origin (2)")]);
        Assert.Null(LibraryProfiles.UniqueExactId(page, "artist", "Origin"));
        Assert.Null(LibraryProfiles.UniqueExactId(page, "label", "Origin"));
    }
}
