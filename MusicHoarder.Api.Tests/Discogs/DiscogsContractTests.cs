using System.Text.Json;
using MusicHoarder.Api.Discogs;

namespace MusicHoarder.Api.Tests.Discogs;

/// <summary>The wire records read the real responses in Fixtures/Discogs without losing a field we keep.</summary>
public class DiscogsContractTests
{
    internal static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Discogs", name));

    private static T Read<T>(string name) => JsonSerializer.Deserialize<T>(Fixture(name), DiscogsJson.Options)!;

    [Fact]
    public void Artist_reads_profile_aliases_variations_urls_and_images()
    {
        var artist = Read<DiscogsArtist>("artist-45-aphex-twin.json");

        Assert.Equal(45, artist.Id);
        Assert.Equal("Aphex Twin", artist.Name);
        Assert.Equal("Richard David James", artist.Realname);
        Assert.False(string.IsNullOrWhiteSpace(artist.Profile));
        Assert.Contains(artist.Aliases!, a => a.Id == 46 && a.Name == "GAK");
        Assert.NotEmpty(artist.Namevariations!);
        Assert.NotEmpty(artist.Urls!);
        var image = artist.Images![0];
        Assert.Equal("primary", image.Type);
        Assert.StartsWith("https://i.discogs.com/", image.Uri);
        Assert.StartsWith("https://i.discogs.com/", image.Uri150);
        Assert.Equal(402, image.Width);
        Assert.Equal(600, image.Height);
    }

    [Fact]
    public void Group_lists_members_and_member_lists_groups()
    {
        var group = Read<DiscogsArtist>("artist-307-boards-of-canada.json");
        var member = Read<DiscogsArtist>("artist-430140-member.json");

        Assert.Contains(group.Members!, m => m.Id == 430140 && m.Active == true);
        Assert.Contains(member.Groups!, g => g.Id == 307);
        Assert.Contains(group.Urls!, u => u.Contains("bandcamp.com", StringComparison.Ordinal));
    }

    [Fact]
    public void Label_reads_parent_sublabels_contact_and_images()
    {
        var label = Read<DiscogsLabel>("label-23528-warp.json");

        Assert.Equal("Warp Records", label.Name);
        Assert.Equal(264514, label.ParentLabel!.Id);
        Assert.Contains(label.Sublabels!, s => s.Id == 16874 && s.Name == "Arcola");
        Assert.False(string.IsNullOrWhiteSpace(label.ContactInfo));
        Assert.NotEmpty(label.Images!);
    }

    [Fact]
    public void Release_reads_labels_companies_formats_identifiers_credits_and_styles()
    {
        var release = Read<DiscogsRelease>("release-249504-vinyl-single.json");

        Assert.Equal(249504, release.Id);
        Assert.Equal(96559, release.MasterId);
        Assert.Equal(1987, release.Year);
        Assert.Equal("UK", release.Country);
        Assert.Equal("1987-07-00", release.Released);
        Assert.Contains(release.Labels!, l => l.Name == "RCA" && l.Catno == "PB 41447" && l.EntityTypeName == "Label");
        Assert.Contains(release.Companies!, c => c.EntityTypeName == "Phonographic Copyright (p)");
        Assert.Equal("Vinyl", release.Formats![0].Name);
        Assert.Contains(release.Identifiers!, i => i.Type == "Barcode" && i.Value == "5012394144777");
        Assert.Contains(release.Extraartists!, e => e.Role == "Design" && e.Anv == "Me Co");
        Assert.Equal("track", release.Tracklist![0].Type);
        Assert.Equal("A", release.Tracklist[0].Position);
        Assert.Equal("3:32", release.Tracklist[0].Duration);
        Assert.NotEmpty(release.Genres!);
        Assert.NotEmpty(release.Styles!);
    }

    [Fact]
    public void Album_release_keeps_repeated_label_with_both_catalog_numbers()
    {
        var release = Read<DiscogsRelease>("release-1545313-cd-album.json");

        Assert.Equal(["AMB 3922 CD", "AMB3922CD"], release.Labels!.Where(l => l.Id == 683).Select(l => l.Catno));
        Assert.Equal(["Electronic"], release.Genres);
        Assert.Equal(["Techno", "IDM", "Ambient", "Experimental"], release.Styles);
        Assert.Contains(release.Identifiers!, i => i.Type == "Matrix / Runout" && i.Description == "Variant 1 - Mirrored");
    }

    [Fact]
    public void Master_reads_main_release_and_tracklist()
    {
        var master = Read<DiscogsMaster>("master-565.json");

        Assert.Equal(1545313, master.MainRelease);
        Assert.Equal(1992, master.Year);
        Assert.Equal("Selected Ambient Works 85-92", master.Title);
        Assert.Equal("Xtal", master.Tracklist![0].Title);
        Assert.Equal(45, master.Artists![0].Id);
    }

    [Theory]
    [InlineData("https://www.discogs.com/release/123-Example", DiscogsResourceKind.Release, 123)]
    [InlineData("https://www.discogs.com/master/565", DiscogsResourceKind.Master, 565)]
    [InlineData("https://www.discogs.com/artist/45", DiscogsResourceKind.Artist, 45)]
    [InlineData("https://www.discogs.com/artist/45-Aphex-Twin", DiscogsResourceKind.Artist, 45)]
    [InlineData("http://discogs.com/label/23528-Warp-Records/", DiscogsResourceKind.Label, 23528)]
    [InlineData("https://www.discogs.com/fr/release/77", DiscogsResourceKind.Release, 77)]
    [InlineData("https://api.discogs.com/releases/249504", DiscogsResourceKind.Release, 249504)]
    public void Discogs_urls_parse(string url, DiscogsResourceKind kind, long id)
    {
        Assert.True(DiscogsUrl.TryParse(url, out var k, out var i));
        Assert.Equal(kind, k);
        Assert.Equal(id, i);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://example.net/release/123")]
    [InlineData("https://www.discogs.com/sell/release/123")]
    [InlineData("https://www.discogs.com/release/abc")]
    [InlineData("not a url")]
    public void Other_urls_are_not_discogs_references(string? url) =>
        Assert.False(DiscogsUrl.TryParse(url, out _, out _));

    [Theory]
    [InlineData("{\"id\":1,\"title\":\"t\",\"year\":\"1992\"}", 1992)]
    [InlineData("{\"id\":1,\"title\":\"t\",\"year\":1992}", 1992)]
    [InlineData("{\"id\":1,\"title\":\"t\",\"year\":0}", null)]
    [InlineData("{\"id\":1,\"title\":\"t\",\"year\":\"\"}", null)]
    public void Years_read_from_numbers_and_strings(string json, int? year) =>
        Assert.Equal(year, JsonSerializer.Deserialize<DiscogsSearchResult>(json, DiscogsJson.Options)!.Year);
}
