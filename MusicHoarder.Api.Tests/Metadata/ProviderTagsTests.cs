using System.Text.Json;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Metadata;

public class ProviderTagsTests
{
    [Fact]
    public void Keeps_original_terms_and_punctuation_with_case_insensitive_deduplication()
    {
        Assert.Equal(new[] { "swedish house", "House", "Folk, World, & Country", "Rock/Pop" },
            ProviderTags.Combine([" swedish house ; House | SWEDISH HOUSE\nFolk, World, & Country", "Rock/Pop"]));
    }

    [Fact]
    public void Match_preserves_raw_tags_and_unions_canonical_genres()
    {
        var song = new SongMetadata { SourcePath = "/test.flac", FileName = "test.flac", Extension = ".flac", FileSizeBytes = 1, LastModifiedUtc = DateTime.UtcNow, IndexedAtUtc = DateTime.UtcNow, Genre = "Techno", Tags = ["existing tag"] };
        var result = Result("swedish house; House") with { Tags = ["swedish house", "House"] };
        MetadataMerger.ApplyMatch(song, result, .99, 2, .96, null);
        Assert.Equal("Techno; House", song.Genre);
        Assert.Equal(new[] { "existing tag", "swedish house", "House" }, song.Tags);
    }

    [Fact]
    public void Review_approval_keeps_raw_terms_in_tags()
    {
        var song = new SongMetadata { SourcePath = "/test.flac", FileName = "test.flac", Extension = ".flac", FileSizeBytes = 1, LastModifiedUtc = DateTime.UtcNow, IndexedAtUtc = DateTime.UtcNow };
        song.ApplyEnrichmentMatch(Result("swedish house; House").ToMatchData(EnrichmentStatus.Matched));
        Assert.Equal("House", song.Genre);
        Assert.Equal(new[] { "swedish house", "House" }, song.Tags);
    }

    [Fact]
    public void Agreed_evidence_unions_terms_but_unrelated_matches_do_not()
    {
        var winner = Result("House");
        var song = new SongMetadata { SourcePath = "/test.flac", FileName = "test.flac", Extension = ".flac", FileSizeBytes = 1, LastModifiedUtc = DateTime.UtcNow, IndexedAtUtc = DateTime.UtcNow, ProviderAttempts = [
            Attempt(EnrichmentProvider.SpotifyAPI, winner),
            Attempt(EnrichmentProvider.Deezer, Result("swedish house; Techno")),
            Attempt(EnrichmentProvider.AppleMusic, Result("Jazz") with { Isrc = "OTHER" })] };
        var merged = ProviderTags.WithEvidence(winner, song.ProviderAttempts, [EnrichmentProvider.SpotifyAPI, EnrichmentProvider.Deezer]);
        Assert.Equal("House; Techno", merged.Genre);
        Assert.Equal(new[] { "House", "swedish house", "Techno" }, merged.Tags);
    }

    [Fact]
    public void Recovery_restores_legacy_evidence_and_ignores_other_recordings_or_corrupt_payloads()
    {
        var attempts = new[] {
            Attempt(EnrichmentProvider.SpotifyAPI, Result("swedish house; House")),
            Attempt(EnrichmentProvider.Deezer, Result("Techno")),
            Attempt(EnrichmentProvider.AppleMusic, Result("Jazz") with { Artist = "Other", Title = "Other", Isrc = "OTHER" }),
            new SongProviderAttempt { Status = ProviderAttemptStatus.Matched, MatchedDataJson = "{broken" } };
        var first = ProviderTags.Recover("House", [], "Artist", "Title", "ISRC", null, null, attempts);
        Assert.Equal(new[] { "swedish house", "House", "Techno" }, first.Tags);
        Assert.Equal("House; Techno", first.Genre);
        var second = ProviderTags.Recover(first.Genre, first.Tags, "Artist", "Title", "ISRC", null, null, attempts);
        Assert.Equal(first.Tags, second.Tags);
        Assert.Equal(first.Genre, second.Genre);
    }

    [Fact]
    public void Recovery_does_not_invent_tags_from_canonical_or_Discogs_only_genres()
    {
        var discogs = Attempt(EnrichmentProvider.Discogs, Result("House; Techno") with { MatchedBy = "Discogs" });
        var recovered = ProviderTags.Recover("House", [], "Artist", "Title", "ISRC", null, null, [discogs]);
        Assert.Empty(recovered.Tags);
        Assert.Equal("House; Techno", recovered.Genre);
    }

    [Fact]
    public void Spotify_artist_genres_keep_original_terms()
    {
        Assert.Equal(new[] { "swedish house", "House" }, MusicHoarder.Api.Spotify.SpotifyCatalogJson.ParseArtistGenres(
            """{"genres":["swedish house","House","SWEDISH HOUSE",null]}"""));
        var track = MusicHoarder.Api.Spotify.SpotifyCatalogJson.ParseTrackDetail(
            """{"id":"track","name":"Title","duration_ms":1000,"artists":[{"id":"exact-artist","name":"Artist"}]}""");
        Assert.Equal("exact-artist", track!.ArtistId);
    }

    [Fact]
    public void Deezer_album_genres_keep_original_terms()
    {
        var album = MusicHoarder.Api.Deezer.DeezerCatalogJson.ParseAlbum(
            """{"id":123,"genres":{"data":[{"name":"Dance"},{"name":"swedish house"}]}}""");
        Assert.Equal(new[] { "Dance", "swedish house" }, album!.Genres);
        var track = MusicHoarder.Api.Deezer.DeezerCatalogJson.ParseTrackDetail(
            """{"id":42,"title":"Title","album":{"id":123,"title":"Album"}}""");
        Assert.Equal("123", track!.AlbumId);
    }

    private static SongProviderAttempt Attempt(EnrichmentProvider provider, EnrichmentProviderResult result) =>
        new() { Provider = provider, Status = ProviderAttemptStatus.Matched, MatchedDataJson = JsonSerializer.Serialize(result) };
    private static EnrichmentProviderResult Result(string genre) => new("Artist", null, "Title", null, null, null, null, null, null, "ISRC", "test", .99, [], EnrichmentStatus.Matched, Genre: genre);
}
