using MusicHoarder.Api.Catalog;

namespace MusicHoarder.Api.Tests.Catalog.Ingestion;

public class CatalogKeysTests
{
    [Theory]
    [InlineData("Aphex Twin", "Aphex Twin")]
    [InlineData("Artist (2)", "Artist")]
    [InlineData("Artist (12)", "Artist")]
    [InlineData("Name *", "Name")]
    [InlineData("Name (3)*", "Name")]
    [InlineData(null, "")]
    public void StripDisambiguator_Removes_Discogs_Suffix(string? input, string expected) =>
        Assert.Equal(expected, CatalogKeys.StripDisambiguator(input));

    [Theory]
    [InlineData("Aphex Twin", "aphex twin")]
    [InlineData("Artist (2)", "artist")]
    [InlineData("Beyoncé", "beyonce")]
    [InlineData("JAY-Z", "jayz")]
    [InlineData("  Extra   Spaces  ", "extra spaces")]
    public void NameKey_Folds_And_Strips_Disambiguator(string input, string expected) =>
        Assert.Equal(expected, CatalogKeys.NameKey(input));

    [Fact]
    public void TitleKey_Matches_NameKey_Rules() =>
        Assert.Equal("selected ambient works 8592", CatalogKeys.TitleKey("Selected Ambient Works 85-92"));

    [Theory]
    [InlineData("5012394144777", true)]
    [InlineData("none", false)]
    [InlineData("None", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("  ", false)]
    public void IsUsableBarcode_Rejects_None(string? value, bool expected) =>
        Assert.Equal(expected, CatalogKeys.IsUsableBarcode(value));

    [Fact]
    public void StableImageKey_Drops_Signature_Segment()
    {
        var a = "https://i.discogs.com/AAA_SIG/rs:fit/g:sm/q:90/h:600/w:402/czM6Ly9kaXNjb2dz/LWRhdGFiYXNl.jpeg";
        var b = "https://i.discogs.com/BBB_OTHER/rs:fit/g:sm/q:90/h:600/w:402/czM6Ly9kaXNjb2dz/LWRhdGFiYXNl.jpeg";

        Assert.Equal(CatalogKeys.StableImageKey(a), CatalogKeys.StableImageKey(b));
        Assert.Equal("/rs:fit/g:sm/q:90/h:600/w:402/czM6Ly9kaXNjb2dz/LWRhdGFiYXNl.jpeg", CatalogKeys.StableImageKey(a));
    }
}
