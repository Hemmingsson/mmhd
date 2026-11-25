using MusicHoarder.Api.Metadata;

namespace MusicHoarder.Api.Tests.Metadata;

public class DiscogsGenresTests
{
    [Theory]
    [InlineData("Ambient / Experimental / Jungle / Lo-fi / Techno, ambient", "Ambient; Experimental; Jungle; Lo-Fi; Techno")]
    [InlineData("RnB / Swing | Funk/Soul; Folk, World, & Country, Funk, Soul", "RnB/Swing; Funk / Soul; Folk, World, & Country; Funk; Soul")]
    [InlineData("Hip-Hop; rap / DNB; house.png", "Hip Hop; Drum n Bass")]
    [InlineData("analog jam; anythinggoes; art.jpeg", null)]
    public void Canonicalizes_only_Discogs_terms(string raw, string? expected) =>
        Assert.Equal(expected, DiscogsGenres.Normalize(raw));
}
