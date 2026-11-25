using Microsoft.Extensions.Options;
using MusicHoarder.Api.Artists;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Catalog.Acquisition;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Catalog.Acquisition;

public class CatalogAcquisitionServiceTests
{
    [Fact]
    public async Task GetAvailability_ScoresDeezerCandidates_AndReportsUnavailableWithoutMatch()
    {
        await using var db = CatalogTestKit.NewContext();
        var graph = CatalogTestKit.SeedDiscogsOnlyRelease(db);
        var deezer = new CatalogFakeDeezer();
        // Wrong title → low score → unavailable
        deezer.Candidates.Add(new DeezerAlbumCandidate("99", "Completely Different", "Someone Else"));
        deezer.Albums["99"] = new DeezerAlbumDetail("99", "Completely Different", "Someone Else", 2020, null,
            [new DeezerAlbumTrackItem(1, 1, "x", 1000, "1")]);

        var sut = Create(db, deezer, new RecordingQueuer());
        var availability = await sut.GetAvailabilityAsync(graph.Release.Id);

        Assert.NotNull(availability);
        Assert.Equal("unavailable", availability.Status);
        Assert.Empty(availability.Editions);
        Assert.Contains(availability.PurchaseLinks, l => l.Url.Contains("bandcamp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetAvailability_KeepsHighConfidenceEdition()
    {
        await using var db = CatalogTestKit.NewContext();
        var graph = CatalogTestKit.SeedDiscogsOnlyRelease(db);
        var deezer = new CatalogFakeDeezer();
        deezer.Candidates.Add(new DeezerAlbumCandidate("55", graph.Release.Title, graph.Release.ArtistDisplay));
        deezer.Albums["55"] = new DeezerAlbumDetail(
            "55", graph.Release.Title, graph.Release.ArtistDisplay, 1993, null,
            [
                new DeezerAlbumTrackItem(1, 1, "Lyot (Reduce)", 420_000, "1"),
                new DeezerAlbumTrackItem(1, 2, "Lyot (Exduce)", 480_000, "2"),
            ]);

        var availability = await Create(db, deezer, new RecordingQueuer()).GetAvailabilityAsync(graph.Release.Id);

        Assert.NotNull(availability);
        Assert.Equal("available", availability.Status);
        var edition = Assert.Single(availability.Editions);
        Assert.Equal("55", edition.EditionId);
        Assert.True(edition.Confidence >= 0.6);
    }

    [Fact]
    public async Task Acquire_Unavailable_QueuesNothing()
    {
        await using var db = CatalogTestKit.NewContext();
        var graph = CatalogTestKit.SeedDiscogsOnlyRelease(db);
        var queuer = new RecordingQueuer();
        var deezer = new CatalogFakeDeezer(); // no candidates

        var result = await Create(db, deezer, queuer).AcquireAsync(CatalogTestKit.OwnerA, graph.Release.Id, null);

        Assert.Equal("unavailable", result.Status);
        Assert.Equal(0, result.Queued);
        Assert.Empty(queuer.Calls);
    }

    [Fact]
    public async Task Acquire_QueuesChosenEdition()
    {
        await using var db = CatalogTestKit.NewContext();
        var graph = CatalogTestKit.SeedDiscogsOnlyRelease(db);
        var deezer = new CatalogFakeDeezer();
        deezer.Candidates.Add(new DeezerAlbumCandidate("77", graph.Release.Title, graph.Release.ArtistDisplay));
        deezer.Candidates.Add(new DeezerAlbumCandidate("78", graph.Release.Title, graph.Release.ArtistDisplay));
        deezer.Albums["77"] = Album(graph, "77");
        deezer.Albums["78"] = Album(graph, "78");
        var queuer = new RecordingQueuer { Result = new ReleaseQueueResult("Lyot Rmx", 1, Queued: 2, 0, 0, 0, 0) };

        var result = await Create(db, deezer, queuer)
            .AcquireAsync(CatalogTestKit.OwnerA, graph.Release.Id, "78");

        Assert.Equal("queued", result.Status);
        Assert.Equal(2, result.Queued);
        Assert.Equal("78", result.ProviderEditionId);
        var call = Assert.Single(queuer.Calls);
        Assert.Equal(["78"], call.EditionIds);
        Assert.Equal(WishlistItemOrigin.ArtistRequest, call.Origin);
        Assert.Equal(CatalogTestKit.OwnerA, call.OwnerId);
    }

    [Theory]
    [InlineData("""["LP","Album","Repress"]""", "Deep Cuts", ArtistReleaseKinds.Album)]
    [InlineData("""["12\"","EP"]""", null, ArtistReleaseKinds.Ep)]
    [InlineData("""["7\"","Maxi-Single"]""", null, ArtistReleaseKinds.Single)]
    [InlineData("""["LP","Compilation"]""", null, ArtistReleaseKinds.Compilation)]
    public async Task Acquire_QueuesTheKindNamedByWholeFormatWords(
        string descriptions, string? text, ArtistReleaseKinds expected)
    {
        await using var db = CatalogTestKit.NewContext();
        var graph = CatalogTestKit.SeedDiscogsOnlyRelease(db);
        var format = db.CatalogReleaseFormats.Local.Single(f => f.ReleaseId == graph.Release.Id);
        format.DescriptionsJson = descriptions;
        format.Text = text;
        graph.Release.FormatSummary = "Vinyl";
        await db.SaveChangesAsync();
        var deezer = new CatalogFakeDeezer();
        deezer.Candidates.Add(new DeezerAlbumCandidate("77", graph.Release.Title, graph.Release.ArtistDisplay));
        deezer.Albums["77"] = Album(graph, "77");
        var queuer = new RecordingQueuer { Result = new ReleaseQueueResult("Lyot Rmx", 1, Queued: 2, 0, 0, 0, 0) };

        await Create(db, deezer, queuer).AcquireAsync(CatalogTestKit.OwnerA, graph.Release.Id, "77");

        Assert.Equal(expected, Assert.Single(queuer.Calls).Kind);
    }

    [Fact]
    public async Task Acquire_AlreadyOwned_WhenAllTracksOwned()
    {
        await using var db = CatalogTestKit.NewContext(CatalogTestKit.OwnerA);
        var graph = CatalogTestKit.SeedDiscogsOnlyRelease(db);
        CatalogTestKit.AddOwnedSong(db, CatalogTestKit.OwnerA, graph.Release, "Lyot (Reduce)");
        CatalogTestKit.AddOwnedSong(db, CatalogTestKit.OwnerA, graph.Release, "Lyot (Exduce)");
        var queuer = new RecordingQueuer();

        var result = await Create(db, new CatalogFakeDeezer(), queuer)
            .AcquireAsync(CatalogTestKit.OwnerA, graph.Release.Id, null);

        Assert.Equal("already-owned", result.Status);
        Assert.Empty(queuer.Calls);
    }

    [Fact]
    public async Task Acquire_NotFound()
    {
        await using var db = CatalogTestKit.NewContext();
        var result = await Create(db, new CatalogFakeDeezer(), new RecordingQueuer())
            .AcquireAsync(CatalogTestKit.OwnerA, 99999, null);
        Assert.Equal("not-found", result.Status);
    }

    [Fact]
    public void Score_RewardsTitleArtistTrackYearUpc()
    {
        var release = new CatalogRelease
        {
            Title = "Lyot Rmx",
            ArtistDisplay = "Basic Channel",
            Year = 1993,
            Tracks =
            [
                new CatalogTrack { Type = "track", Title = "A" },
                new CatalogTrack { Type = "track", Title = "B" },
            ],
            Identifiers =
            [
                new CatalogReleaseIdentifier { Type = "Barcode", Value = "1234567890123" },
                new CatalogReleaseIdentifier { Type = "Barcode", Value = "none" },
            ],
        };

        var score = CatalogAcquisitionService.Score(
            release, "Lyot Rmx", "Basic Channel", 2, 1993, "1234567890123");
        Assert.Equal(1.0, score, 3);

        var noneUpc = CatalogAcquisitionService.Score(
            release, "Lyot Rmx", "Basic Channel", 2, 1993, "none");
        Assert.True(noneUpc < 1.0);
        Assert.True(noneUpc >= 0.6);
    }

    private static DeezerAlbumDetail Album(CatalogTestKit.CatalogGraph graph, string id) =>
        new(id, graph.Release.Title, graph.Release.ArtistDisplay, 1993, null,
        [
            new DeezerAlbumTrackItem(1, 1, "Lyot (Reduce)", 420_000, $"{id}-1"),
            new DeezerAlbumTrackItem(1, 2, "Lyot (Exduce)", 480_000, $"{id}-2"),
        ]);

    private static CatalogAcquisitionService Create(
        MusicHoarderDbContext db, IDeezerCatalogService deezer, ICatalogEditionQueuer queuer) =>
        new(db, deezer, queuer, Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/src",
            DestinationDirectory = "/dst",
            ArtistRequestMaxTracks = 100,
        }));

    private sealed class RecordingQueuer : ICatalogEditionQueuer
    {
        public List<(Guid OwnerId, string Artist, IReadOnlyList<string> EditionIds, ArtistReleaseKinds Kind, WishlistItemOrigin Origin)> Calls { get; } = [];
        public ReleaseQueueResult Result { get; set; } = new("t", 1, 1, 0, 0, 0, 0);

        public Task<ReleaseQueueResult> QueueAsync(
            Guid ownerId, string artistName, IReadOnlyList<string> editionIds, ArtistReleaseKinds kind,
            WishlistItemOrigin origin, int trackBudget, bool requeueFailed, CancellationToken ct)
        {
            Calls.Add((ownerId, artistName, editionIds, kind, origin));
            return Task.FromResult(Result);
        }
    }

    private sealed class CatalogFakeDeezer : IDeezerCatalogService
    {
        public List<DeezerAlbumCandidate> Candidates { get; } = [];
        public Dictionary<string, DeezerAlbumDetail> Albums { get; } = new(StringComparer.Ordinal);

        public Task<DeezerCatalogTrack?> LookupByIsrcAsync(string isrc, CancellationToken ct = default) =>
            Task.FromResult<DeezerCatalogTrack?>(null);
        public Task<IReadOnlyList<DeezerCatalogTrack>> SearchTracksAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeezerCatalogTrack>>([]);
        public Task<DeezerCatalogTrack?> LookupByIdAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<DeezerCatalogTrack?>(null);
        public Task<string?> SearchAlbumIdAsync(string artist, string album, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task<IReadOnlyList<DeezerAlbumCandidate>> SearchAlbumCandidatesAsync(string artist, string album, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeezerAlbumCandidate>>(Candidates);
        public Task<IReadOnlyList<DeezerArtistCandidate>> SearchArtistCandidatesAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeezerArtistCandidate>>([]);
        public Task<DeezerAlbumDetail?> GetAlbumAsync(string albumId, CancellationToken ct = default) =>
            Task.FromResult(Albums.GetValueOrDefault(albumId));
        public Task<DeezerPlaylistSummary?> GetPlaylistAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<DeezerPlaylistSummary?>(null);
        public Task<DeezerPlaylistTracksResult> GetPlaylistTracksAsync(string id, int? maxTracks = null, CancellationToken ct = default) =>
            Task.FromResult(new DeezerPlaylistTracksResult([], true));
    }
}
