using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.AlbumTracklist;
using MusicHoarder.Api.Enrichment.AlbumTracklist.Providers;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Discogs;

namespace MusicHoarder.Api.Tests.Enrichment;

public class DiscogsEnrichmentProviderTests
{
    [Fact]
    public async Task NameSearch_ResolvesTheMastersMainRelease_WithoutEditionFields()
    {
        var client = new FixtureDiscogsClient();
        var provider = Provider(client, out var ingestor);

        var outcome = await provider.TryEnrichAsync(Song());

        var result = Assert.IsType<ProviderMatched>(outcome).Result;
        Assert.Equal("Selected Ambient Works 85-92", result.Album);
        Assert.Equal("Aphex Twin", result.AlbumArtist);
        Assert.Equal("Xtal", result.Title);
        Assert.Equal(1992, result.Year);
        Assert.Equal("Techno; IDM; Ambient; Experimental", result.Genre);
        Assert.Equal(565, result.DiscogsMasterId);
        Assert.Equal(45, result.DiscogsArtistId);
        // A name match knows the album, not which pressing the file is: no label/catno/barcode/release id.
        Assert.Null(result.Label);
        Assert.Null(result.CatalogNumber);
        Assert.Null(result.Upc);
        Assert.Null(result.DiscogsReleaseId);
        Assert.Equal(EnrichmentStatus.Matched, result.RecommendedStatus);
        Assert.Equal(["search:release", "master:565", "release:1545313"], client.Calls);
        Assert.Equal([565L], ingestor.Masters);
        Assert.Equal([1545313L], ingestor.Releases);
    }

    [Fact]
    public async Task KnownReleaseId_IsEditionExact()
    {
        var client = new FixtureDiscogsClient();
        var song = Song();
        song.DiscogsReleaseId = 1545313;

        var result = Assert.IsType<ProviderMatched>(await Provider(client, out _).TryEnrichAsync(song)).Result;

        Assert.Equal("Apollo", result.Label);
        Assert.Equal("AMB 3922 CD", result.CatalogNumber);
        Assert.Null(result.Upc); // the release's barcode is "none"
        Assert.Equal(1545313, result.DiscogsReleaseId);
        Assert.Equal("Discogs:release-id", result.MatchedBy);
        Assert.DoesNotContain(client.Calls, c => c.StartsWith("search", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecondSongOfTheAlbum_CostsNoRequests()
    {
        var client = new FixtureDiscogsClient();
        var provider = Provider(client, out _);
        await provider.TryEnrichAsync(Song());
        var calls = client.Calls.Count;

        var other = Song();
        other.Title = "Tha";
        var result = Assert.IsType<ProviderMatched>(await provider.TryEnrichAsync(other)).Result;

        Assert.Equal("Tha", result.Title);
        Assert.Equal(calls, client.Calls.Count);
    }

    [Fact]
    public async Task UnknownAlbum_IsNoMatch()
    {
        var song = Song();
        song.Album = "Drukqs";
        Assert.IsType<ProviderNoMatch>(await Provider(new FixtureDiscogsClient(), out _).TryEnrichAsync(song));
    }

    [Fact]
    public async Task TrackMissingFromTheRelease_KeepsTheAlbumButWarns()
    {
        // The overlay is album-level: a bonus track not on the main release still has its label and styles.
        var song = Song();
        song.Title = "Windowlicker";
        var result = Assert.IsType<ProviderMatched>(await Provider(new FixtureDiscogsClient(), out _).TryEnrichAsync(song)).Result;
        Assert.Contains("track_not_on_release", result.MatchWarnings);
        Assert.Equal("Windowlicker", result.Title);
        Assert.True(result.MatchConfidence < 0.9);
    }

    [Fact]
    public async Task ContradictingTracklist_IsOnlyACandidate()
    {
        var song = Song();
        song.TotalTracks = 30;
        song.Year = 2001;
        song.Title = "Windowlicker";
        var noMatch = Assert.IsType<ProviderNoMatch>(await Provider(new FixtureDiscogsClient(), out _).TryEnrichAsync(song));
        Assert.Equal(EnrichmentStatus.NeedsReview, noMatch.BestCandidate!.RecommendedStatus);
    }

    [Fact]
    public async Task RateLimit_IsReportedAsRateLimited()
    {
        var client = new FixtureDiscogsClient { RateLimited = true };
        var outcome = await Provider(client, out _).TryEnrichAsync(Song());
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.IsType<ProviderRateLimited>(outcome).RetryAfter);
    }

    [Fact]
    public async Task NearlySpentAllowance_IsRateLimitedWithoutCallingTheClient()
    {
        var client = new FixtureDiscogsClient
        {
            LastRateLimit = new DiscogsRateLimitSnapshot(60, 57, 3, DateTime.UtcNow.AddSeconds(-10)),
        };

        var outcome = await Provider(client, out _).TryEnrichAsync(Song());

        var retryAfter = Assert.IsType<ProviderRateLimited>(outcome).RetryAfter;
        Assert.InRange(retryAfter, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        Assert.Empty(client.Calls);
    }

    [Theory]
    [InlineData(6, -10)]  // above the reserve
    [InlineData(0, -61)]  // spent, but over a minute ago: the window has moved on
    [InlineData(null, -1)] // no remaining header
    public async Task AllowanceThatIsNotNearlySpent_StillCallsTheClient(int? remaining, int observedSecondsAgo)
    {
        var client = new FixtureDiscogsClient
        {
            LastRateLimit = new DiscogsRateLimitSnapshot(60, null, remaining, DateTime.UtcNow.AddSeconds(observedSecondsAgo)),
        };

        Assert.IsType<ProviderMatched>(await Provider(client, out _).TryEnrichAsync(Song()));
        Assert.NotEmpty(client.Calls);
    }

    [Fact]
    public void Switches_GateTheProvider()
    {
        var client = new FixtureDiscogsClient();
        Assert.False(Provider(client, out _, enabled: false).IsEnabled);
        Assert.True(Provider(client, out _).IsEnabled);

        var anonymous = new FixtureDiscogsClient { Authenticated = false };
        var provider = Provider(anonymous, out _);
        Assert.False(provider.CanHandle(Song()));
        var linked = Song();
        linked.DiscogsReleaseId = 1;
        Assert.True(provider.CanHandle(linked));
    }

    [Fact]
    public async Task TracklistProvider_MapsTheMainRelease()
    {
        var client = new FixtureDiscogsClient();
        var tracklist = new DiscogsAlbumTracklistProvider(Matcher(client), client, Monitor(new DiscogsOptions { EnableTracklistProvider = true }));

        Assert.True(tracklist.IsEnabled(new MusicEnricherOptions()));
        var candidate = await tracklist.FetchAsync(new AlbumQuery("Aphex Twin", "Selected Ambient Works 85-92", null, null, [], 13));

        Assert.NotNull(candidate);
        Assert.Equal(EnrichmentProvider.Discogs, candidate.Source);
        Assert.Equal("1545313", candidate.ProviderAlbumId);
        Assert.Equal(13, candidate.Tracks.Count);
        Assert.Equal("Xtal", candidate.Tracks[0].Title);
        Assert.Equal(Enumerable.Range(1, 13), candidate.Tracks.Select(t => t.TrackNumber));
        Assert.All(candidate.Tracks, t => Assert.Equal(1, t.DiscNumber));
    }

    [Fact]
    public void TracklistProvider_IsOffByDefault()
    {
        var client = new FixtureDiscogsClient();
        Assert.False(new DiscogsAlbumTracklistProvider(Matcher(client), client, Monitor(new DiscogsOptions()))
            .IsEnabled(new MusicEnricherOptions()));
    }

    [Theory]
    [InlineData("A1", 1, null)]
    [InlineData("7", 1, 7)]
    [InlineData("2-3", 2, 3)]
    [InlineData("CD2-11", 2, 11)]
    [InlineData("1.4", 1, 4)]
    [InlineData(null, 1, null)]
    public void Positions(string? position, int disc, int? track)
        => Assert.Equal((disc, track), DiscogsTracklist.Position(position));

    [Fact]
    public void Flatten_DropsHeadingsAndCountsIndexTracksOnce()
    {
        var tracks = DiscogsTracklist.Flatten(
        [
            new DiscogsTrack(null, "heading", "Side One"),
            new DiscogsTrack("A1", "track", "Opening", "3:05"),
            new DiscogsTrack("A2", "index", "Suite", null, SubTracks:
            [
                new DiscogsTrack("A2a", "track", "Part 1", "1:00"),
                new DiscogsTrack("A2b", "track", "Part 2", "2:00"),
            ]),
            new DiscogsTrack("B1", "track", "Closing", "1:02:03"),
        ]);

        Assert.Equal(["Opening", "Suite", "Closing"], tracks.Select(t => t.Title));
        Assert.Equal([1, 2, 3], tracks.Select(t => t.TrackNumber));
        Assert.Equal([185_000, 180_000, 3_723_000], tracks.Select(t => t.DurationMs ?? 0));
    }

    [Theory]
    [InlineData("none", null)]
    [InlineData("5 411659 169225", "5411659169225")]
    [InlineData("[Four Manufacturing Services logo] FOURMANUFACTURING.COM A0103398328-0101 12 A00", null)]
    [InlineData("", null)]
    public void Barcodes(string value, string? expected) => Assert.Equal(expected, DiscogsReleaseMatcher.UsableBarcode(value));

    [Theory]
    [InlineData("Aphex Twin (2)", "Aphex Twin")]
    [InlineData("Apollo*", "Apollo")]
    [InlineData("AFX", "AFX")]
    public void DisplayNames(string name, string expected) => Assert.Equal(expected, DiscogsReleaseMatcher.DisplayName(name));

    private static SongMetadata Song() => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = "/m/Aphex Twin/SAW/01 Xtal.flac",
        FileName = "01 Xtal.flac",
        Extension = ".flac",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = "Aphex Twin",
        AlbumArtist = "Aphex Twin",
        Album = "Selected Ambient Works 85-92",
        Title = "Xtal",
        Year = 1992,
        TotalTracks = 13,
    };

    private static DiscogsEnrichmentProvider Provider(
        FixtureDiscogsClient client, out RecordingIngestor ingestor, bool enabled = true)
    {
        ingestor = new RecordingIngestor();
        var services = new ServiceCollection().AddSingleton<ICatalogIngestor>(ingestor).BuildServiceProvider();
        return new DiscogsEnrichmentProvider(
            Matcher(client), client, Monitor(new DiscogsOptions { EnableEnrichmentProvider = enabled }),
            services.GetRequiredService<IServiceScopeFactory>(), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DiscogsEnrichmentProvider>.Instance);
    }

    private static DiscogsReleaseMatcher Matcher(IDiscogsClient client)
        => new(client, new MemoryCache(new MemoryCacheOptions()), Monitor(new DiscogsOptions()), NullLogger<DiscogsReleaseMatcher>.Instance);

    private static IOptionsMonitor<DiscogsOptions> Monitor(DiscogsOptions value) => new StaticMonitor(value);

    private sealed class StaticMonitor(DiscogsOptions value) : IOptionsMonitor<DiscogsOptions>
    {
        public DiscogsOptions CurrentValue => value;
        public DiscogsOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<DiscogsOptions, string?> listener) => null;
    }

    internal sealed class RecordingIngestor : ICatalogIngestor
    {
        public List<long> Masters { get; } = [];
        public List<long> Releases { get; } = [];

        public Task<CatalogArtist> UpsertArtistAsync(DiscogsDocument<DiscogsArtist> document, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CatalogLabel> UpsertLabelAsync(DiscogsDocument<DiscogsLabel> document, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<CatalogMaster> UpsertMasterAsync(DiscogsDocument<DiscogsMaster> document, CancellationToken ct = default)
        {
            Masters.Add(document.Value.Id);
            return Task.FromResult(new CatalogMaster { DiscogsId = document.Value.Id, Title = document.Value.Title });
        }

        public Task<CatalogRelease> UpsertReleaseAsync(DiscogsDocument<DiscogsRelease> document, CancellationToken ct = default)
        {
            Releases.Add(document.Value.Id);
            return Task.FromResult(new CatalogRelease { DiscogsId = document.Value.Id, Title = document.Value.Title });
        }

        public Task<int> UpsertArtistDiscographyAsync(int artistId, IReadOnlyList<DiscogsArtistReleaseEntry> entries, bool complete, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> UpsertLabelReleasesAsync(int labelId, IReadOnlyList<DiscogsLabelReleaseEntry> entries, bool complete, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> UpsertMasterVersionsAsync(int masterId, IReadOnlyList<DiscogsMasterVersion> versions, bool complete, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkUnavailableAsync(CatalogEntityKind kind, long discogsId, string? error, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Answers from the recorded responses in Fixtures/Discogs and logs every request.</summary>
    internal sealed class FixtureDiscogsClient : IDiscogsClient
    {
        public bool Authenticated { get; init; } = true;
        public bool RateLimited { get; init; }
        public List<string> Calls { get; } = [];

        public bool IsEnabled => true;
        public bool IsAuthenticated => Authenticated;
        public DiscogsRateLimitSnapshot? LastRateLimit { get; init; }

        public Task<DiscogsDocument<DiscogsArtist>?> GetArtistAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DiscogsDocument<DiscogsLabel>?> GetLabelAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<DiscogsDocument<DiscogsMaster>?> GetMasterAsync(long id, CancellationToken ct = default)
            => Answer<DiscogsMaster>($"master:{id}", id == 565 ? "master-565.json" : null);

        public Task<DiscogsDocument<DiscogsRelease>?> GetReleaseAsync(long id, CancellationToken ct = default)
            => Answer<DiscogsRelease>($"release:{id}", id switch
            {
                1545313 => "release-1545313-cd-album.json",
                249504 => "release-249504-vinyl-single.json",
                _ => null,
            });

        public Task<DiscogsPage<DiscogsArtistReleaseEntry>?> GetArtistReleasesAsync(long id, int page = 1, int perPage = 100, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DiscogsPage<DiscogsLabelReleaseEntry>?> GetLabelReleasesAsync(long id, int page = 1, int perPage = 100, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DiscogsPage<DiscogsMasterVersion>?> GetMasterVersionsAsync(long id, int page = 1, int perPage = 100, DiscogsVersionFilter? filter = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<DiscogsPage<DiscogsSearchResult>> SearchAsync(DiscogsSearchQuery query, int page = 1, int perPage = 25, CancellationToken ct = default)
        {
            Calls.Add($"search:{query.Type}");
            if (RateLimited)
                throw new ProviderRateLimitedException(TimeSpan.FromSeconds(30));

            var empty = new DiscogsPage<DiscogsSearchResult>(new DiscogsPagination(1, 1, perPage, 0), []);
            if (query.ReleaseTitle != "Selected Ambient Works 85-92")
                return Task.FromResult(empty);

            using var doc = JsonDocument.Parse(DiscogsContractTests.Fixture("search-release-saw-85-92.json"));
            var items = doc.RootElement.GetProperty("results").Deserialize<List<DiscogsSearchResult>>(DiscogsJson.Options)!;
            return Task.FromResult(new DiscogsPage<DiscogsSearchResult>(new DiscogsPagination(1, 1, perPage, items.Count), items));
        }

        public Task<DiscogsImageBytes?> DownloadImageAsync(string signedUrl, CancellationToken ct = default) => throw new NotSupportedException();

        private Task<DiscogsDocument<T>?> Answer<T>(string call, string? fixture)
        {
            Calls.Add(call);
            if (RateLimited)
                throw new ProviderRateLimitedException(TimeSpan.FromSeconds(30));
            if (fixture is null)
                return Task.FromResult<DiscogsDocument<T>?>(null);
            var json = DiscogsContractTests.Fixture(fixture);
            return Task.FromResult<DiscogsDocument<T>?>(new DiscogsDocument<T>(JsonSerializer.Deserialize<T>(json, DiscogsJson.Options)!, json));
        }
    }
}
