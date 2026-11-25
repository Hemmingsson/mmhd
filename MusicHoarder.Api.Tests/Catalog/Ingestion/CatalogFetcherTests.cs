using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Catalog.Ingestion;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Catalog.Ingestion;

public class CatalogFetcherTests
{
    private static (CatalogFetcher Fetcher, FakeDiscogsClient Client, CatalogIngestionTestKit.FixedTimeProvider Clock, MusicHoarderDbContext Db)
        Create(Action<FakeDiscogsClient>? configure = null)
    {
        var db = CatalogIngestionTestKit.NewContext();
        var clock = CatalogIngestionTestKit.Clock();
        var opts = CatalogIngestionTestKit.Options();
        var client = new FakeDiscogsClient();
        configure?.Invoke(client);
        var ingestor = new CatalogIngestor(db, clock, CatalogIngestionTestKit.Monitor(opts));
        var fetcher = new CatalogFetcher(client, ingestor, db, clock, CatalogIngestionTestKit.Monitor(opts),
            NullLogger<CatalogFetcher>.Instance);
        return (fetcher, client, clock, db);
    }

    [Fact]
    public async Task EnsureArtist_Returns_Cached_When_Fresh()
    {
        var (fetcher, client, _, _) = Create(c =>
        {
            c.Artists[45] = CatalogIngestionTestKit.Document<DiscogsArtist>("artist-45-aphex-twin.json");
        });

        var first = await fetcher.EnsureArtistAsync(45, withDiscography: false);
        Assert.NotNull(first);
        Assert.Equal(1, client.ArtistCalls);

        var second = await fetcher.EnsureArtistAsync(45, withDiscography: false);
        Assert.Same(first, second);
        Assert.Equal(1, client.ArtistCalls);
    }

    [Fact]
    public async Task EnsureArtist_Force_Refetches()
    {
        var (fetcher, client, _, _) = Create(c =>
        {
            c.Artists[45] = CatalogIngestionTestKit.Document<DiscogsArtist>("artist-45-aphex-twin.json");
        });

        await fetcher.EnsureArtistAsync(45, withDiscography: false);
        await fetcher.EnsureArtistAsync(45, withDiscography: false, force: true);
        Assert.Equal(2, client.ArtistCalls);
    }

    [Fact]
    public async Task EnsureArtist_WithDiscography_Pages_Listing()
    {
        var page = JsonPage();
        var (fetcher, client, _, db) = Create(c =>
        {
            c.Artists[45] = CatalogIngestionTestKit.Document<DiscogsArtist>("artist-45-aphex-twin.json");
            c.ArtistReleases[45] = [page];
        });

        var artist = await fetcher.EnsureArtistAsync(45, withDiscography: true);
        Assert.NotNull(artist);
        Assert.Equal(1, client.ArtistReleaseCalls);
        Assert.Equal(5, artist!.DiscographyItemCount);
        Assert.Equal(5, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(
            db.CatalogDiscographyEntries.Where(e => e.OwnerId == artist.Id)));
    }

    [Fact]
    public async Task EnsureArtist_404_Marks_Unavailable_And_Returns_Null()
    {
        var (fetcher, client, _, db) = Create(c =>
        {
            c.NotFoundArtists.Add(40404);
            c.Throw404AsException = true;
        });

        var result = await fetcher.EnsureArtistAsync(40404, withDiscography: false);
        Assert.Null(result);
        var row = Assert.Single(db.CatalogArtists.Where(a => a.DiscogsId == 40404));
        Assert.Equal(CatalogFetchState.NotFound, row.FetchState);
        Assert.Equal(1, client.ArtistCalls);
    }

    [Fact]
    public async Task EnsureArtist_Disabled_Returns_Existing_Or_Null()
    {
        var (fetcher, client, clock, db) = Create(c =>
        {
            c.Artists[45] = CatalogIngestionTestKit.Document<DiscogsArtist>("artist-45-aphex-twin.json");
        });

        Assert.Null(await fetcher.EnsureArtistAsync(99, withDiscography: false));

        await fetcher.EnsureArtistAsync(45, withDiscography: false);
        client.IsEnabled = false;
        client.ArtistCalls = 0; // reset via new property - need to allow reset
        // Can't reset private setter easily; just check no new fetch by comparing row
        var cached = await fetcher.EnsureArtistAsync(45, withDiscography: false, force: true);
        Assert.NotNull(cached);
        Assert.Equal(CatalogFetchState.Fetched, cached!.FetchState);
        // force would fetch if enabled; disabled returns existing without calling
        Assert.Equal(0, client.ArtistCalls);

        _ = clock;
        _ = db;
    }

    [Fact]
    public async Task EnsureRelease_Also_Ensures_Master()
    {
        var (fetcher, client, _, _) = Create(c =>
        {
            c.Releases[249504] = CatalogIngestionTestKit.Document<DiscogsRelease>("release-249504-vinyl-single.json");
            c.Masters[96559] = new DiscogsDocument<DiscogsMaster>(
                new DiscogsMaster(96559, "Never Gonna Give You Up", Year: 1987, MainRelease: 249504,
                    Artists: [new DiscogsArtistRef(72872, "Rick Astley")]),
                """{"id":96559,"title":"Never Gonna Give You Up"}""");
        });

        var release = await fetcher.EnsureReleaseAsync(249504);
        Assert.NotNull(release);
        Assert.Equal(1, client.ReleaseCalls);
        Assert.Equal(1, client.MasterCalls);
    }

    private static DiscogsPage<DiscogsArtistReleaseEntry> JsonPage()
    {
        var json = CatalogIngestionTestKit.FixtureJson("artist-45-releases-page1.json");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var items = System.Text.Json.JsonSerializer.Deserialize<List<DiscogsArtistReleaseEntry>>(
            doc.RootElement.GetProperty("releases").GetRawText(), DiscogsJson.Options)!;
        var pagination = System.Text.Json.JsonSerializer.Deserialize<DiscogsPagination>(
            doc.RootElement.GetProperty("pagination").GetRawText(), DiscogsJson.Options)!;
        // Fixture is page 1 of many; treat as complete single page for the test.
        return new DiscogsPage<DiscogsArtistReleaseEntry>(
            new DiscogsPagination(1, 1, pagination.PerPage, items.Count), items);
    }
}
