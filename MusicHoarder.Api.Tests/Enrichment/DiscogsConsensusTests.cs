using System.Text.Json;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

/// <summary>Discogs supports consensus without ever moving a verdict.</summary>
public class DiscogsConsensusTests
{
    private static readonly IdentityMatchOptions Opts = IdentityMatchOptions.Default;

    private static readonly EnrichmentProvider[] Voting =
    [
        EnrichmentProvider.AcoustID, EnrichmentProvider.MusicBrainzWeb, EnrichmentProvider.SpotifyAPI,
        EnrichmentProvider.Deezer, EnrichmentProvider.AppleMusic,
    ];

    /// <summary>Scenarios across every consensus tier; each must reach the same verdict with Discogs in play.</summary>
    public static TheoryData<string> Scenarios() =>
    [
        "solo-fingerprint", "solo-name", "cluster", "cluster-contradicted", "two-name-conflict",
        "pending", "rate-limited", "all-no-match", "review-only",
    ];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void DiscogsNeverChangesTheVerdict(string scenario)
    {
        var enabled = new HashSet<EnrichmentProvider>(Voting);
        var withDiscogs = new HashSet<EnrichmentProvider>(Voting) { EnrichmentProvider.Discogs };

        var baseline = ConsensusEvaluator.Evaluate(Build(scenario), enabled, Opts);

        foreach (var discogs in DiscogsVariants())
        {
            var song = Build(scenario);
            discogs(song);
            var r = ConsensusEvaluator.Evaluate(song, withDiscogs, Opts, applicableProviders: withDiscogs);

            Assert.Equal(baseline.Status, r.Status);
            Assert.Equal(baseline.AgreeingProviders, r.AgreeingProviders);
            Assert.Equal(baseline.Confidence, r.Confidence);
            Assert.Equal(baseline.Winner?.Title, r.Winner?.Title);
            Assert.Equal(baseline.Winner?.Artist, r.Winner?.Artist);
            Assert.DoesNotContain(EnrichmentProvider.Discogs, r.AgreeingProviders);
        }
    }

    [Fact]
    public void DiscogsAlone_IsNotAVerdict()
    {
        var song = Song();
        Add(song, EnrichmentProvider.Discogs, ProviderAttemptStatus.Matched, Discogs());

        var r = ConsensusEvaluator.Evaluate(song, Enabled(EnrichmentProvider.Discogs, EnrichmentProvider.SpotifyAPI), Opts);

        Assert.Equal(EnrichmentStatus.Pending, r.Status);
        Assert.Null(r.Winner);
    }

    [Fact]
    public void UnattemptedDiscogs_DoesNotKeepASongPending()
    {
        var song = Song();
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.NoMatch, null);

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.SpotifyAPI, EnrichmentProvider.Discogs), Opts);

        Assert.Equal(EnrichmentStatus.NeedsReview, r.Status);
    }

    [Fact]
    public void MatchedWinnerOfTheSameAlbum_GetsDiscogsCatalogFields()
    {
        var song = Song();
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Winner("SpotifyAPI", label: "Spotify Label", upc: "111111111111", genre: "electronic"));
        Add(song, EnrichmentProvider.Discogs, ProviderAttemptStatus.Matched, Discogs());

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.SpotifyAPI, EnrichmentProvider.Discogs), Opts);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
        Assert.Equal([EnrichmentProvider.SpotifyAPI], r.AgreeingProviders);
        Assert.Equal("Apollo", r.Winner!.Label);
        Assert.Equal("AMB 3922 CD", r.Winner.CatalogNumber);
        Assert.Equal("111111111111", r.Winner.Upc); // a streaming UPC names the file's edition: kept
        Assert.Equal("Techno; IDM; Ambient; Experimental", r.Winner.Genre);
        Assert.Equal(1545313, r.Winner.DiscogsReleaseId);
        Assert.Equal(565, r.Winner.DiscogsMasterId);
        Assert.Equal(45, r.Winner.DiscogsArtistId);
        Assert.Equal(new HashSet<string> { "Label", "CatalogNumber", "Genre" }, r.SupportingFields);
        Assert.Equal("Selected Ambient Works 85-92", r.Winner.Album);
        Assert.Equal("Xtal", r.Winner.Title);
    }

    [Fact]
    public void ClusterCorroboration_DoesNotCoverFieldsDiscogsSupplies()
    {
        var song = Song();
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched, Winner("SpotifyAPI", label: "Other", isrc: "GBBPW9200001"));
        Add(song, EnrichmentProvider.Deezer, ProviderAttemptStatus.Matched, Winner("Deezer", label: "Other", isrc: "GBBPW9200001"));
        Add(song, EnrichmentProvider.Discogs, ProviderAttemptStatus.Matched, Discogs());

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.SpotifyAPI, EnrichmentProvider.Deezer, EnrichmentProvider.Discogs), Opts);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
        Assert.Equal("Apollo", r.Winner!.Label);
        Assert.NotNull(r.CorroboratedFields);
        Assert.DoesNotContain("Label", r.CorroboratedFields!);
    }

    [Theory]
    [InlineData("Another Album", "Aphex Twin")]
    [InlineData("Selected Ambient Works 85-92", "Someone Else")]
    public void DiscogsReleaseOfAnotherAlbum_IsIgnored(string album, string artist)
    {
        var song = Song();
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched, Winner("SpotifyAPI", label: "Spotify Label"));
        Add(song, EnrichmentProvider.Discogs, ProviderAttemptStatus.Matched, Discogs() with { Album = album, AlbumArtist = artist, Artist = artist });

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.SpotifyAPI, EnrichmentProvider.Discogs), Opts);

        Assert.Equal("Spotify Label", r.Winner!.Label);
        Assert.Null(r.Winner.DiscogsReleaseId);
        Assert.Null(r.SupportingFields);
    }

    [Fact]
    public void DisambiguatedDiscogsArtist_StillMatchesTheAlbum()
    {
        var song = Song();
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched, Winner("SpotifyAPI"));
        Add(song, EnrichmentProvider.Discogs, ProviderAttemptStatus.Matched, Discogs() with { AlbumArtist = "Aphex Twin (2)*" });

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.SpotifyAPI, EnrichmentProvider.Discogs), Opts);

        Assert.Equal("Apollo", r.Winner!.Label);
    }

    [Fact]
    public void LowConfidenceDiscogs_OverlaysNothing()
    {
        var song = Song();
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched, Winner("SpotifyAPI", label: "Spotify Label"));
        Add(song, EnrichmentProvider.Discogs, ProviderAttemptStatus.NoMatch,
            Discogs() with { RecommendedStatus = EnrichmentStatus.NeedsReview, MatchConfidence = 0.6 });

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.SpotifyAPI, EnrichmentProvider.Discogs), Opts);

        Assert.Equal("Spotify Label", r.Winner!.Label);
    }

    [Fact]
    public void DisabledDiscogs_LeftoverAttemptOverlaysNothing()
    {
        var song = Song();
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched, Winner("SpotifyAPI", label: "Spotify Label"));
        Add(song, EnrichmentProvider.Discogs, ProviderAttemptStatus.Matched, Discogs());

        var r = ConsensusEvaluator.Evaluate(song, Enabled(EnrichmentProvider.SpotifyAPI), Opts);

        Assert.Equal("Spotify Label", r.Winner!.Label);
    }

    [Fact]
    public void FreshDiscogsRateLimit_DoesNotDeferTheVerdict()
    {
        var song = Song();
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.NoMatch, null);
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = EnrichmentProvider.Discogs,
            Status = ProviderAttemptStatus.RateLimited,
            RateLimitedSinceUtc = DateTime.UtcNow,
        });

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.SpotifyAPI, EnrichmentProvider.Discogs), Opts);

        Assert.Equal(EnrichmentStatus.NeedsReview, r.Status);
    }

    [Theory]
    [InlineData("Electronic; Techno; IDM; Ambient; Experimental", true)]
    [InlineData("Electronic; Techno; IDM; Ambient; Experimental; Rock", true)]
    [InlineData("Techno; IDM; Ambient; Experimental", true)]
    [InlineData("My Favourite Ambient", false)]
    [InlineData("Electronic; Techno; Braindance", false)]
    [InlineData(null, false)]
    public void StoredGenreOfBroadGenresPlusStyles_MayGiveWayToStyles(string? stored, bool expected)
        => Assert.Equal(expected, FieldAuthority.IsStylesOfStoredGenre(stored, "Techno; IDM; Ambient; Experimental"));

    private static IEnumerable<Action<SongMetadata>> DiscogsVariants()
    {
        yield return s => Add(s, EnrichmentProvider.Discogs, ProviderAttemptStatus.Matched, Discogs());
        // A Discogs candidate naming a different recording and album is not a dissenter.
        yield return s => Add(s, EnrichmentProvider.Discogs, ProviderAttemptStatus.Matched,
            Discogs() with { Title = "Something Else", Artist = "Other", AlbumArtist = "Other", Album = "Elsewhere" });
        yield return s => Add(s, EnrichmentProvider.Discogs, ProviderAttemptStatus.NoMatch, null);
        yield return s => s.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = EnrichmentProvider.Discogs,
            Status = ProviderAttemptStatus.RateLimited,
            RateLimitedSinceUtc = DateTime.UtcNow,
        });
        yield return _ => { }; // enabled, applicable, not yet attempted
    }

    private static SongMetadata Build(string scenario)
    {
        var song = Song();
        switch (scenario)
        {
            case "solo-fingerprint":
                foreach (var p in Voting.Skip(1))
                    Add(song, p, ProviderAttemptStatus.NoMatch, null);
                Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
                    Winner("AcoustID", mbid: "mb-1"));
                break;
            case "solo-name":
                Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched, Winner("SpotifyAPI"));
                foreach (var p in Voting.Where(p => p != EnrichmentProvider.SpotifyAPI))
                    Add(song, p, ProviderAttemptStatus.NoMatch, null);
                break;
            case "cluster":
                Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched, Winner("SpotifyAPI", isrc: "GBBPW9200001"));
                Add(song, EnrichmentProvider.Deezer, ProviderAttemptStatus.Matched, Winner("Deezer", isrc: "GBBPW9200001"));
                Add(song, EnrichmentProvider.MusicBrainzWeb, ProviderAttemptStatus.Matched, Winner("MusicBrainzWeb", mbid: "mb-1"));
                break;
            case "cluster-contradicted":
                Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched, Winner("AcoustID", mbid: "mb-1"));
                Add(song, EnrichmentProvider.MusicBrainzWeb, ProviderAttemptStatus.Matched, Winner("MusicBrainzWeb", mbid: "mb-1"));
                Add(song, EnrichmentProvider.AppleMusic, ProviderAttemptStatus.Matched,
                    Winner("AppleMusic") with { Title = "Completely Different Song", Artist = "Another Artist" });
                break;
            case "two-name-conflict":
                Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched, Winner("SpotifyAPI"));
                Add(song, EnrichmentProvider.Deezer, ProviderAttemptStatus.Matched,
                    Winner("Deezer") with { Title = "Not Xtal", Artist = "Not Aphex" });
                break;
            case "pending":
                Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
                    Winner("AcoustID", mbid: "mb-1") with { RecommendedStatus = EnrichmentStatus.NeedsReview, MatchConfidence = 0.6 });
                break;
            case "rate-limited":
                Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
                    Winner("AcoustID", mbid: "mb-1") with { RecommendedStatus = EnrichmentStatus.NeedsReview, MatchConfidence = 0.6 });
                song.ProviderAttempts.Add(new SongProviderAttempt
                {
                    Provider = EnrichmentProvider.AppleMusic,
                    Status = ProviderAttemptStatus.RateLimited,
                    RateLimitedSinceUtc = DateTime.UtcNow,
                });
                break;
            case "all-no-match":
                foreach (var p in Voting)
                    Add(song, p, ProviderAttemptStatus.NoMatch, null);
                break;
            case "review-only":
                foreach (var p in Voting.Skip(1))
                    Add(song, p, ProviderAttemptStatus.NoMatch, null);
                Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
                    Winner("AcoustID", mbid: "mb-1") with { RecommendedStatus = EnrichmentStatus.NeedsReview, MatchConfidence = 0.7 });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        return song;
    }

    private static EnrichmentProviderResult Winner(
        string matchedBy, string? mbid = null, string? isrc = null, string? label = null, string? upc = null, string? genre = null)
        => new("Aphex Twin", "Aphex Twin", "Xtal", 1992, 1, mbid, null,
            matchedBy == "SpotifyAPI" ? "spot-1" : null, null, isrc, matchedBy, 0.95, [], EnrichmentStatus.Matched,
            Album: "Selected Ambient Works 85-92", DurationSeconds: 294, Label: label, Upc: upc, Genre: genre);

    private static EnrichmentProviderResult Discogs()
        => new("Aphex Twin", "Aphex Twin", "Xtal", 1992, null, null, null, null, null, null,
            "Discogs:search", 0.95, [], EnrichmentStatus.Matched,
            Album: "Selected Ambient Works 85-92", Genre: "Techno; IDM; Ambient; Experimental",
            Label: "Apollo", CatalogNumber: "AMB 3922 CD", Upc: "5411659169225",
            DiscogsReleaseId: 1545313, DiscogsMasterId: 565, DiscogsArtistId: 45);

    private static SongMetadata Song() => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = "/x.mp3",
        FileName = "x.mp3",
        Extension = ".mp3",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        EnrichmentStatus = EnrichmentStatus.Pending,
        Artist = "Aphex Twin",
        Title = "Xtal",
        Album = "Selected Ambient Works 85-92",
    };

    private static IReadOnlySet<EnrichmentProvider> Enabled(params EnrichmentProvider[] p) => new HashSet<EnrichmentProvider>(p);

    private static void Add(SongMetadata song, EnrichmentProvider provider, ProviderAttemptStatus status, EnrichmentProviderResult? candidate)
        => song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = provider,
            Status = status,
            AttemptedAtUtc = DateTime.UtcNow,
            MatchedDataJson = candidate is null ? null : JsonSerializer.Serialize(candidate),
        });
}
