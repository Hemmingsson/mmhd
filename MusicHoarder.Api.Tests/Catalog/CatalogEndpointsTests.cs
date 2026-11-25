using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Artists;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Catalog.Acquisition;
using MusicHoarder.Api.Catalog.Query;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Catalog.Acquisition;

namespace MusicHoarder.Api.Tests.Catalog;

public class CatalogEndpointsTests
{
    [Fact]
    public async Task GetRelease_ReturnsOk_ForSeededGraph()
    {
        await using var db = CatalogTestKit.NewContext();
        var graph = CatalogTestKit.SeedDiscogsOnlyRelease(db);
        var catalog = new CatalogQueryService(db);

        var result = await CatalogEndpoints.GetRelease(graph.Release.Id, catalog, default);

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        var dto = Value<CatalogReleaseDetailDto>(result);
        Assert.Equal(graph.Release.Title, dto.Title);
    }

    [Fact]
    public async Task LibraryArtist_ReturnsIdOr404()
    {
        await using var db = CatalogTestKit.NewContext(CatalogTestKit.OwnerA);
        var graph = CatalogTestKit.SeedDiscogsOnlyRelease(db);
        CatalogTestKit.AddOwnedSong(db, CatalogTestKit.OwnerA, graph.Release, "A", graph.Artist.DiscogsId);
        var catalog = new CatalogQueryService(db);

        var found = await CatalogEndpoints.FindLibraryArtist("Basic Channel", catalog, default);
        Assert.Equal(StatusCodes.Status200OK, Status(found));

        var missing = await CatalogEndpoints.FindLibraryArtist("Nobody", catalog, default);
        Assert.Equal(StatusCodes.Status404NotFound, Status(missing));
    }

    [Fact]
    public async Task Acquire_Returns409_WhenUnavailable_AndDoesNotQueue()
    {
        await using var db = CatalogTestKit.NewContext();
        var graph = CatalogTestKit.SeedDiscogsOnlyRelease(db);
        var queuer = new SpyQueuer();
        var acquisition = new CatalogAcquisitionService(
            db, new EmptyDeezer(), queuer,
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { SourceDirectory = "/s", DestinationDirectory = "/d" }));

        var result = await CatalogEndpoints.Acquire(
            graph.Release.Id, null, acquisition, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser), default);

        Assert.Equal(StatusCodes.Status409Conflict, Status(result));
        var body = Value<CatalogAcquireResultDto>(result);
        Assert.Equal("unavailable", body.Status);
        Assert.Equal(0, body.Queued);
        Assert.Empty(queuer.Calls);
    }

    [Fact]
    public async Task Acquire_Returns200_WhenQueued()
    {
        await using var db = CatalogTestKit.NewContext();
        var graph = CatalogTestKit.SeedDiscogsOnlyRelease(db);
        var deezer = new MatchingDeezer(graph);
        var queuer = new SpyQueuer { Result = new ReleaseQueueResult(graph.Release.Title, 1, 3, 0, 0, 0, 0) };
        var acquisition = new CatalogAcquisitionService(
            db, deezer, queuer,
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { SourceDirectory = "/s", DestinationDirectory = "/d", ArtistRequestMaxTracks = 50 }));

        var result = await CatalogEndpoints.Acquire(
            graph.Release.Id,
            new CatalogEndpoints.CatalogAcquireRequest("55"),
            acquisition,
            new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser),
            default);

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        var body = Value<CatalogAcquireResultDto>(result);
        Assert.Equal("queued", body.Status);
        Assert.Equal(3, body.Queued);
        Assert.Equal("55", Assert.Single(queuer.Calls));
    }

    [Fact]
    public async Task Acquire_Returns404_WhenMissing()
    {
        await using var db = CatalogTestKit.NewContext();
        var acquisition = new CatalogAcquisitionService(
            db, new EmptyDeezer(), new SpyQueuer(),
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { SourceDirectory = "/s", DestinationDirectory = "/d" }));

        var result = await CatalogEndpoints.Acquire(
            404, null, acquisition, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser), default);

        Assert.Equal(StatusCodes.Status404NotFound, Status(result));
    }

    [Fact]
    public async Task GetImage_StreamsDownloadedFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mh-catalog-img-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "cover.jpg");
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);

        await using var db = CatalogTestKit.NewContext();
        var graph = CatalogTestKit.SeedDiscogsOnlyRelease(db);
        var image = new CatalogImage
        {
            EntityKind = CatalogEntityKind.Release,
            EntityId = graph.Release.Id,
            Sequence = 1,
            Type = "secondary",
            SourceUrl = "https://img.example/x.jpg",
            LocalPath = "cover.jpg",
            ContentType = "image/jpeg",
            Status = CatalogImageStatus.Downloaded,
            SeenAtUtc = DateTime.UtcNow,
            DownloadedAtUtc = DateTime.UtcNow,
        };
        db.CatalogImages.Add(image);
        db.SaveChanges();

        var http = new DefaultHttpContext();
        var result = await CatalogEndpoints.GetImage(
            image.Id, db, Microsoft.Extensions.Options.Options.Create(new DiscogsOptions { ImageDirectory = dir }), http, default);

        var fileResult = Assert.IsType<PhysicalFileHttpResult>(result);
        Assert.Equal("image/jpeg", fileResult.ContentType);
        Assert.Equal("private, max-age=604800", http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task ListReleases_Endpoint_ReturnsPage()
    {
        await using var db = CatalogTestKit.NewContext();
        CatalogTestKit.SeedDiscogsOnlyRelease(db);
        var result = await CatalogEndpoints.ListReleases(new CatalogReleaseQuery(), new CatalogQueryService(db), default);
        Assert.Equal(StatusCodes.Status200OK, Status(result));
        Assert.NotEmpty(Value<CatalogPage<CatalogReleaseCardDto>>(result).Items);
    }

    private static int Status(IResult result) =>
        result is IStatusCodeHttpResult s ? s.StatusCode ?? 0 : 0;

    private static T Value<T>(IResult result) =>
        (T)((IValueHttpResult)result).Value!;

    private sealed class SpyQueuer : ICatalogEditionQueuer
    {
        public List<string> Calls { get; } = [];
        public ReleaseQueueResult Result { get; set; } = new("t", 1, 1, 0, 0, 0, 0);

        public Task<ReleaseQueueResult> QueueAsync(
            Guid ownerId, string artistName, IReadOnlyList<string> editionIds, ArtistReleaseKinds kind,
            WishlistItemOrigin origin, int trackBudget, bool requeueFailed, CancellationToken ct)
        {
            Calls.AddRange(editionIds);
            return Task.FromResult(Result);
        }
    }

    private sealed class EmptyDeezer : IDeezerCatalogService
    {
        public Task<DeezerCatalogTrack?> LookupByIsrcAsync(string isrc, CancellationToken ct = default) =>
            Task.FromResult<DeezerCatalogTrack?>(null);
        public Task<IReadOnlyList<DeezerCatalogTrack>> SearchTracksAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeezerCatalogTrack>>([]);
        public Task<DeezerCatalogTrack?> LookupByIdAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<DeezerCatalogTrack?>(null);
        public Task<string?> SearchAlbumIdAsync(string artist, string album, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task<IReadOnlyList<DeezerAlbumCandidate>> SearchAlbumCandidatesAsync(string artist, string album, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeezerAlbumCandidate>>([]);
        public Task<IReadOnlyList<DeezerArtistCandidate>> SearchArtistCandidatesAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeezerArtistCandidate>>([]);
        public Task<DeezerAlbumDetail?> GetAlbumAsync(string albumId, CancellationToken ct = default) =>
            Task.FromResult<DeezerAlbumDetail?>(null);
        public Task<DeezerPlaylistSummary?> GetPlaylistAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<DeezerPlaylistSummary?>(null);
        public Task<DeezerPlaylistTracksResult> GetPlaylistTracksAsync(string id, int? maxTracks = null, CancellationToken ct = default) =>
            Task.FromResult(new DeezerPlaylistTracksResult([], true));
    }

    private sealed class MatchingDeezer(CatalogTestKit.CatalogGraph graph) : IDeezerCatalogService
    {
        public Task<DeezerCatalogTrack?> LookupByIsrcAsync(string isrc, CancellationToken ct = default) =>
            Task.FromResult<DeezerCatalogTrack?>(null);
        public Task<IReadOnlyList<DeezerCatalogTrack>> SearchTracksAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeezerCatalogTrack>>([]);
        public Task<DeezerCatalogTrack?> LookupByIdAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<DeezerCatalogTrack?>(null);
        public Task<string?> SearchAlbumIdAsync(string artist, string album, CancellationToken ct = default) =>
            Task.FromResult<string?>("55");
        public Task<IReadOnlyList<DeezerAlbumCandidate>> SearchAlbumCandidatesAsync(string artist, string album, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeezerAlbumCandidate>>(
                [new DeezerAlbumCandidate("55", graph.Release.Title, graph.Release.ArtistDisplay)]);
        public Task<IReadOnlyList<DeezerArtistCandidate>> SearchArtistCandidatesAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeezerArtistCandidate>>([]);
        public Task<DeezerAlbumDetail?> GetAlbumAsync(string albumId, CancellationToken ct = default) =>
            Task.FromResult<DeezerAlbumDetail?>(new DeezerAlbumDetail(
                albumId, graph.Release.Title, graph.Release.ArtistDisplay, 1993, null,
                [
                    new DeezerAlbumTrackItem(1, 1, "a", 1, "1"),
                    new DeezerAlbumTrackItem(1, 2, "b", 1, "2"),
                ]));
        public Task<DeezerPlaylistSummary?> GetPlaylistAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<DeezerPlaylistSummary?>(null);
        public Task<DeezerPlaylistTracksResult> GetPlaylistTracksAsync(string id, int? maxTracks = null, CancellationToken ct = default) =>
            Task.FromResult(new DeezerPlaylistTracksResult([], true));
    }
}
