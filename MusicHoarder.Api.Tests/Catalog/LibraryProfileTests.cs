using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Discogs;

namespace MusicHoarder.Api.Tests.Catalog;

public class LibraryProfileTests
{
    [Theory]
    [InlineData("Swedish independent label founded by [a=Example Artist] and based in Malmö, in the southern-most part of Sweden.", "SE")]
    [InlineData("Independent record label based in Germany.", "DE")]
    [InlineData("Based in Paris, France.", "FR")]
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
