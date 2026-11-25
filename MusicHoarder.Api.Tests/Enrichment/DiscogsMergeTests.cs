using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

/// <summary>Discogs' fields fill gaps; a curated value is only ever proposed against.</summary>
public class DiscogsMergeTests
{
    private static readonly IReadOnlySet<string> Supporting = new HashSet<string> { "Label", "CatalogNumber", "Genre" };

    [Fact]
    public void GoodExistingLabel_IsProposedNotReplaced_EvenUnderHighConsensus()
    {
        var song = Song();
        song.Label = "Curated Label";

        var changes = MetadataMerger.ApplyMatch(song, Winner(), 0.99, 3, 0.9, null, null, Supporting);

        Assert.Equal("Curated Label", song.Label);
        var label = Assert.Single(changes, c => c.Field == "Label");
        Assert.False(label.Applied);
        Assert.Equal("Apollo", label.NewValue);
    }

    [Fact]
    public void EmptyFields_AreFilled()
    {
        var song = Song();

        var changes = MetadataMerger.ApplyMatch(song, Winner(), 0.95, 1, 0.9, null, null, Supporting);

        Assert.Equal("Apollo", song.Label);
        Assert.Equal("AMB 3922 CD", song.CatalogNumber);
        Assert.Equal("Techno; IDM; Ambient", song.Genre);
        Assert.Equal(1545313, song.DiscogsReleaseId);
        Assert.Equal(565, song.DiscogsMasterId);
        Assert.Equal(45, song.DiscogsArtistId);
        Assert.All(changes.Where(c => c.Field.StartsWith("Discogs", StringComparison.Ordinal)), c => Assert.True(c.Applied));
    }

    [Fact]
    public void StoredGenresAndIncomingStyles_AreCombined()
    {
        var song = Song();
        song.Genre = "Electronic; Techno; IDM; Ambient";

        MetadataMerger.ApplyMatch(song, Winner(), 0.95, 1, 0.9, null, null, Supporting);

        Assert.Equal("Electronic; Techno; IDM; Ambient", song.Genre);
    }

    [Fact]
    public void ACustomLegacyGenre_IsPreservedAsATagWhileCanonicalStylesAreApplied()
    {
        var song = Song();
        song.Genre = "Late Night Listening";

        var changes = MetadataMerger.ApplyMatch(song, Winner(), 0.99, 3, 0.9, null, null, Supporting);

        Assert.Equal("Techno; IDM; Ambient", song.Genre);
        Assert.Contains("Late Night Listening", song.Tags);
        Assert.True(Assert.Single(changes, c => c.Field == "Genre").Applied);
    }

    [Fact]
    public void LinkedRelease_IsNeverRepointed()
    {
        var song = Song();
        song.DiscogsReleaseId = 999;

        var changes = MetadataMerger.ApplyMatch(song, Winner(), 0.99, 3, 0.9, null, null, Supporting);

        Assert.Equal(999, song.DiscogsReleaseId);
        Assert.DoesNotContain(changes, c => c.Field == "DiscogsReleaseId");
    }

    [Fact]
    public void WithoutSupportingFields_TheMergeIsUnchanged()
    {
        var song = Song();
        song.Label = "Curated Label";

        MetadataMerger.ApplyMatch(song, Winner(), 0.99, 3, 0.9, null);

        Assert.Equal("Apollo", song.Label); // the existing high-consensus upgrade path
    }

    [Theory]
    [InlineData("Label", true)]
    [InlineData("DiscogsMasterId", true)]
    [InlineData("Title", false)]
    public void SupportingChanges_AreAttributedToDiscogs(string field, bool expected)
        => Assert.Equal(expected, MetadataMerger.IsSupportingChange(field, Supporting));

    private static EnrichmentProviderResult Winner()
        => new("Aphex Twin", "Aphex Twin", "Xtal", 1992, 1, null, null, "spot-1", null, null,
            "SpotifyAPI", 0.95, [], EnrichmentStatus.Matched,
            Album: "Selected Ambient Works 85-92", Genre: "Techno; IDM; Ambient", Label: "Apollo",
            CatalogNumber: "AMB 3922 CD", DiscogsReleaseId: 1545313, DiscogsMasterId: 565, DiscogsArtistId: 45);

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
    };
}
