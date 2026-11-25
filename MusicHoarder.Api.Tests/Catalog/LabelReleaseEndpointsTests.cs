using Microsoft.AspNetCore.Http;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Tests.Catalog.Ingestion;

namespace MusicHoarder.Api.Tests.Catalog;

public class LabelReleaseEndpointsTests
{
    [Fact]
    public async Task LabelPageIncludesUncachedEditionsAndUserScopedOwnership()
    {
        await using var db = CatalogTestKit.NewContext(CatalogTestKit.OwnerA);
        var graph = CatalogTestKit.SeedDiscogsOnlyRelease(db);
        CatalogTestKit.AddOwnedSong(db, CatalogTestKit.OwnerB, graph.Release, "Other user's copy");
        var client = new FakeDiscogsClient();
        client.LabelReleases[graph.Label.DiscogsId] = [
            new(new(1, 2, 100, 101), [new(999, "New edition", "Artist", Year: 2026)]),
            new(new(2, 2, 100, 101), [new(graph.Release.DiscogsId, graph.Release.Title, Year: 1993)])
        ];

        var first = await CatalogEndpoints.ListDiscogsLabelReleases(graph.Label.DiscogsId, 1, client, db, default);
        var firstPage = Assert.IsType<DiscogsPage<CatalogDiscographyItemDto>>(((IValueHttpResult)first).Value);
        Assert.Equal(101, firstPage.Pagination.Items);
        Assert.Equal(2, firstPage.Pagination.Pages);
        var remote = Assert.Single(firstPage.Items);
        Assert.Equal(999, remote.DiscogsId);
        Assert.Null(remote.ReleaseId);

        var second = await CatalogEndpoints.ListDiscogsLabelReleases(graph.Label.DiscogsId, 2, client, db, default);
        var secondPage = Assert.IsType<DiscogsPage<CatalogDiscographyItemDto>>(((IValueHttpResult)second).Value);
        var local = Assert.Single(secondPage.Items);
        Assert.Equal(graph.Release.Id, local.ReleaseId);
        Assert.False(local.Owned);

        CatalogTestKit.AddOwnedSong(db, CatalogTestKit.OwnerA, graph.Release, "My copy");
        var owned = await CatalogEndpoints.ListDiscogsLabelReleases(graph.Label.DiscogsId, 2, client, db, default);
        Assert.True(Assert.Single(Assert.IsType<DiscogsPage<CatalogDiscographyItemDto>>(((IValueHttpResult)owned).Value).Items).Owned);
    }

    [Fact]
    public async Task InvalidPageIsRejected()
    {
        await using var db = CatalogTestKit.NewContext();
        var result = await CatalogEndpoints.ListDiscogsLabelReleases(2001, 0, new FakeDiscogsClient(), db, default);
        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
    }
}
