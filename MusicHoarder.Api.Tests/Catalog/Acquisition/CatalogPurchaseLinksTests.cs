using MusicHoarder.Api.Catalog.Acquisition;

namespace MusicHoarder.Api.Tests.Catalog.Acquisition;

public class CatalogPurchaseLinksTests
{
    [Theory]
    [InlineData("https://artist.bandcamp.com/album/x", "purchase")]
    [InlineData("https://www.qobuz.com/album/x", "purchase")]
    [InlineData("https://www.7digital.com/x", "purchase")]
    [InlineData("https://www.beatport.com/release/x", "purchase")]
    [InlineData("https://www.juno.co.uk/x", "purchase")]
    [InlineData("https://boomkat.com/products/x", "purchase")]
    [InlineData("https://music.apple.com/album/x", "purchase")]
    [InlineData("https://itunes.apple.com/album/x", "purchase")]
    [InlineData("https://www.amazon.com/dp/x", "purchase")]
    [InlineData("https://open.spotify.com/album/x", "streaming")]
    [InlineData("https://www.deezer.com/album/x", "streaming")]
    [InlineData("https://tidal.com/album/x", "streaming")]
    [InlineData("https://www.youtube.com/watch?v=x", "streaming")]
    [InlineData("https://soundcloud.com/x", "streaming")]
    [InlineData("https://www.basicchannel.com", "info")]
    public void Classify_MapsKnownHosts(string url, string kind) =>
        Assert.Equal(kind, CatalogPurchaseLinks.Classify(url));

    [Fact]
    public void Build_AlwaysAddsDiscogsPageAndMarketplace_AndDedupes()
    {
        var links = CatalogPurchaseLinks.Build(42,
        [
            "https://artist.bandcamp.com/album/x",
            "https://artist.bandcamp.com/album/x",
            "https://open.spotify.com/album/x",
        ]);

        Assert.Contains(links, l => l.Kind == "purchase" && l.Url.Contains("bandcamp", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(links, l => l.Kind == "streaming" && l.Url.Contains("spotify", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(links, l => l.Name == "Discogs" && l.Kind == "info" && l.Url.EndsWith("/release/42"));
        Assert.Contains(links, l => l.Name == "Discogs Marketplace" && l.Kind == "purchase" && l.Url.Contains("/sell/release/42"));
        Assert.Equal(1, links.Count(l => l.Url.Contains("bandcamp", StringComparison.OrdinalIgnoreCase)));
    }
}
