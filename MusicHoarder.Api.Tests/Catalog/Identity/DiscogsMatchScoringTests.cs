using MusicHoarder.Api.Catalog.Identity;
using MusicHoarder.Api.Discogs;

namespace MusicHoarder.Api.Tests.Catalog.Identity;

public class DiscogsMatchScoringTests
{
    [Fact]
    public void TitlesEqual_StripsDisambiguatorAndCase()
    {
        Assert.True(DiscogsMatchScoring.TitlesEqual("Selected Ambient Works 85-92", "selected ambient works 85-92"));
        Assert.True(DiscogsMatchScoring.ArtistsEqual("Aphex Twin (2)", "Aphex Twin*"));
        Assert.False(DiscogsMatchScoring.ArtistsEqual("Aphex Twin", "AFX"));
    }

    [Fact]
    public void DurationsMatch_WithinThreeSeconds()
    {
        Assert.True(DiscogsMatchScoring.DurationsMatch(100, 103));
        Assert.True(DiscogsMatchScoring.DurationsMatch(100, 97));
        Assert.False(DiscogsMatchScoring.DurationsMatch(100, 104));
        Assert.False(DiscogsMatchScoring.DurationsMatch(null, 100));
    }

    [Theory]
    [InlineData("3:45", 225)]
    [InlineData("1:02:03", 3723)]
    [InlineData("90", 90)]
    [InlineData("", null)]
    [InlineData("bad", null)]
    public void ParseDurationSeconds(string input, int? expected) =>
        Assert.Equal(expected, DiscogsMatchScoring.ParseDurationSeconds(input));

    [Fact]
    public void ScoreRelease_RequiresArtistAndTitle_ThenBoostsTracklistYearBarcode()
    {
        var library = new DiscogsMatchScoring.LibraryAlbumHint(
            "Aphex Twin",
            "Selected Ambient Works 85-92",
            1992,
            "5021608021821",
            "WARP CD 21",
            "Warp Records",
            [
                new("Xtal", 291),
                new("Tha", 546),
            ]);

        var release = new DiscogsRelease(
            Id: 1,
            Title: "Selected Ambient Works 85-92",
            Year: 1992,
            Artists: [new DiscogsArtistRef(45, "Aphex Twin")],
            Labels: [new DiscogsLabelRef(23528, "Warp Records", Catno: "WARP CD 21")],
            Identifiers: [new DiscogsIdentifier("Barcode", "5 021608 021821")],
            Tracklist:
            [
                new DiscogsTrack("1", "track", "Xtal", "4:51"),
                new DiscogsTrack("2", "track", "Tha", "9:06"),
            ]);

        var score = DiscogsMatchScoring.ScoreRelease(library, release);
        Assert.True(score >= 0.9, $"expected high confidence, got {score}");

        var wrongArtist = release with { Artists = [new DiscogsArtistRef(1, "Someone Else")] };
        Assert.Equal(0, DiscogsMatchScoring.ScoreRelease(library, wrongArtist));
    }

    [Fact]
    public void ScoreSearchHit_BarcodeRaisesConfidence()
    {
        var library = new DiscogsMatchScoring.LibraryAlbumHint(
            "A", "Album", 2000, "1234567890123", null, null, []);
        var hit = new DiscogsSearchResult(
            Id: 9,
            Type: "release",
            Title: "A - Album",
            Year: 2000,
            Barcode: ["1234567890123"]);
        Assert.True(DiscogsMatchScoring.ScoreSearchHit(library, hit) >= 0.7);
    }

    [Fact]
    public void AlbumSubjectKey_UsesUnitSeparator()
    {
        var key = DiscogsMatchScoring.AlbumSubjectKey("Aphex Twin", "SAW");
        Assert.Contains('\u001f', key);
        Assert.StartsWith(DiscogsMatchScoring.ArtistSubjectKey("Aphex Twin"), key);
    }
}
