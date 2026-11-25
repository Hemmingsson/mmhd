using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Catalog.Ingestion;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Catalog.Ingestion;

public class CatalogIngestorTests
{
    private static CatalogIngestor Create(MusicHoarderDbContext db, CatalogIngestionTestKit.FixedTimeProvider? clock = null)
    {
        clock ??= CatalogIngestionTestKit.Clock();
        return new CatalogIngestor(db, clock, CatalogIngestionTestKit.Monitor(CatalogIngestionTestKit.Options()));
    }

    [Fact]
    public async Task UpsertArtist_Writes_Profile_Relations_Urls_Images_And_Stubs()
    {
        await using var db = CatalogIngestionTestKit.NewContext();
        var ingestor = Create(db);

        var artist = await ingestor.UpsertArtistAsync(CatalogIngestionTestKit.Document<DiscogsArtist>("artist-45-aphex-twin.json"));

        Assert.Equal(45, artist.DiscogsId);
        Assert.Equal("Aphex Twin", artist.Name);
        Assert.Equal("aphex twin", artist.NameKey);
        Assert.Equal("Richard David James", artist.RealName);
        Assert.Equal(CatalogFetchState.Fetched, artist.FetchState);
        Assert.False(string.IsNullOrWhiteSpace(artist.Profile));
        Assert.NotEmpty(artist.NameVariations);
        Assert.Contains(artist.Relations, r => r.Kind == CatalogArtistRelationKind.Alias && r.RelatedDiscogsId == 46);

        Assert.True(await db.CatalogUrls.AnyAsync(u => u.EntityKind == CatalogEntityKind.Artist && u.EntityId == artist.Id));
        Assert.True(await db.CatalogImages.AnyAsync(i => i.EntityKind == CatalogEntityKind.Artist && i.EntityId == artist.Id && i.Status == CatalogImageStatus.Remote));
        Assert.True(await db.CatalogArtists.AnyAsync(a => a.DiscogsId == 46 && a.FetchState == CatalogFetchState.Listed));
        Assert.Equal(1, await db.CatalogRawSnapshots.CountAsync(s => s.ResourceKind == "artist" && s.DiscogsId == 45));
    }

    [Fact]
    public async Task UpsertArtist_Is_Idempotent_And_Keeps_One_Snapshot()
    {
        await using var db = CatalogIngestionTestKit.NewContext();
        var ingestor = Create(db);
        var doc = CatalogIngestionTestKit.Document<DiscogsArtist>("artist-45-aphex-twin.json");

        await ingestor.UpsertArtistAsync(doc);
        var variations = await db.CatalogArtistNameVariations.CountAsync();
        var relations = await db.CatalogArtistRelations.CountAsync();
        var images = await db.CatalogImages.CountAsync();
        var urls = await db.CatalogUrls.CountAsync();

        await ingestor.UpsertArtistAsync(doc);

        Assert.Equal(variations, await db.CatalogArtistNameVariations.CountAsync());
        Assert.Equal(relations, await db.CatalogArtistRelations.CountAsync());
        Assert.Equal(images, await db.CatalogImages.CountAsync());
        Assert.Equal(urls, await db.CatalogUrls.CountAsync());
        Assert.Equal(1, await db.CatalogRawSnapshots.CountAsync(s => s.ResourceKind == "artist" && s.DiscogsId == 45));
    }

    [Fact]
    public async Task UpsertLabel_Writes_Parent_Sublabels_And_Stubs()
    {
        await using var db = CatalogIngestionTestKit.NewContext();
        var ingestor = Create(db);

        var label = await ingestor.UpsertLabelAsync(CatalogIngestionTestKit.Document<DiscogsLabel>("label-23528-warp.json"));

        Assert.Equal("Warp Records", label.Name);
        Assert.Equal(264514, label.ParentDiscogsId);
        Assert.Contains(label.Sublabels, s => s.DiscogsId == 16874);
        Assert.True(await db.CatalogLabels.AnyAsync(l => l.DiscogsId == 264514 && l.FetchState == CatalogFetchState.Listed));
        Assert.True(await db.CatalogLabels.AnyAsync(l => l.DiscogsId == 16874 && l.FetchState == CatalogFetchState.Listed));
    }

    [Fact]
    public async Task UpsertMaster_Stores_Genres_As_Json_And_Styles_As_Rows()
    {
        await using var db = CatalogIngestionTestKit.NewContext();
        var ingestor = Create(db);

        var master = await ingestor.UpsertMasterAsync(CatalogIngestionTestKit.Document<DiscogsMaster>("master-565.json"));

        Assert.Equal("Selected Ambient Works 85-92", master.Title);
        Assert.Equal(1992, master.Year);
        Assert.Equal(1545313, master.MainReleaseDiscogsId);
        Assert.Contains("Electronic", master.GenresJson);
        Assert.Equal(0, await db.CatalogTracks.CountAsync()); // masters do not store tracklist
        Assert.True(await db.CatalogStyles.AnyAsync(s => s.Name == "Techno"));
        Assert.True(await db.CatalogMasterStyles.AnyAsync(s => s.MasterId == master.Id));
        Assert.True(await db.CatalogReleases.AnyAsync(r => r.DiscogsId == 1545313 && r.FetchState == CatalogFetchState.Listed));
        Assert.True(await db.CatalogArtists.AnyAsync(a => a.DiscogsId == 45 && a.FetchState == CatalogFetchState.Listed));
    }

    [Fact]
    public async Task UpsertRelease_Keeps_Repeated_Label_Catnos_And_Barcode_None()
    {
        await using var db = CatalogIngestionTestKit.NewContext();
        var ingestor = Create(db);

        var release = await ingestor.UpsertReleaseAsync(CatalogIngestionTestKit.Document<DiscogsRelease>("release-1545313-cd-album.json"));

        var labelRows = await db.CatalogReleaseLabels
            .Where(l => l.ReleaseId == release.Id && l.Kind == CatalogReleaseLabelKind.Label && l.LabelDiscogsId == 683)
            .Select(l => l.CatalogNumber)
            .ToListAsync();
        Assert.Equal(["AMB 3922 CD", "AMB3922CD"], labelRows);

        Assert.Contains("Electronic", release.GenresJson);
        Assert.False(await db.CatalogStyles.AnyAsync(s => s.Name == "Electronic"));
        Assert.Equal(4, await db.CatalogReleaseStyles.CountAsync(s => s.ReleaseId == release.Id));

        var barcode = await db.CatalogReleaseIdentifiers.SingleAsync(i =>
            i.ReleaseId == release.Id && i.Type == "Barcode");
        Assert.Equal("none", barcode.Value);
        Assert.False(CatalogKeys.IsUsableBarcode(barcode.Value));

        Assert.Equal(13, await db.CatalogTracks.CountAsync(t => t.ReleaseId == release.Id));
        Assert.Equal(4 * 60 + 51, (await db.CatalogTracks.SingleAsync(t => t.ReleaseId == release.Id && t.Sequence == 0)).DurationSeconds);
        Assert.True(await db.CatalogArtists.AnyAsync(a => a.DiscogsId == 45));
        Assert.True(await db.CatalogLabels.AnyAsync(l => l.DiscogsId == 683 && l.FetchState == CatalogFetchState.Listed));
        Assert.True(await db.CatalogMasters.AnyAsync(m => m.DiscogsId == release.MasterDiscogsId));
    }

    [Fact]
    public async Task UpsertRelease_Parses_Vinyl_Sides_And_Extra_Credits()
    {
        await using var db = CatalogIngestionTestKit.NewContext();
        var ingestor = Create(db);

        var release = await ingestor.UpsertReleaseAsync(CatalogIngestionTestKit.Document<DiscogsRelease>("release-249504-vinyl-single.json"));

        var tracks = await db.CatalogTracks.Where(t => t.ReleaseId == release.Id).OrderBy(t => t.Sequence).ToListAsync();
        Assert.Equal(2, tracks.Count);
        Assert.Equal(1, tracks[0].DiscNumber);
        Assert.Equal(2, tracks[1].DiscNumber);
        Assert.Equal(212, tracks[0].DurationSeconds);

        Assert.Contains(await db.CatalogCredits.Where(c => c.ReleaseId == release.Id).ToListAsync(),
            c => c.Kind == CatalogCreditKind.Extra && c.Role == "Design" && c.Anv == "Me Co");
        Assert.Equal("Vinyl, 7\", 45 RPM, Single, Stereo", release.FormatSummary);
        Assert.Contains("PB 41447", release.CatalogNumberSummary);
    }

    [Fact]
    public async Task Discography_Complete_Removes_Missing_And_Creates_Browsable_Stubs()
    {
        await using var db = CatalogIngestionTestKit.NewContext();
        var ingestor = Create(db);
        var artist = await ingestor.UpsertArtistAsync(CatalogIngestionTestKit.Document<DiscogsArtist>("artist-45-aphex-twin.json"));

        var page = JsonSerializer.Deserialize<ArtistReleasesPage>(
            CatalogIngestionTestKit.FixtureJson("artist-45-releases-page1.json"), DiscogsJson.Options)!;

        await ingestor.UpsertArtistDiscographyAsync(artist.Id, page.Releases, complete: true);

        Assert.Equal(5, await db.CatalogDiscographyEntries.CountAsync(e => e.OwnerId == artist.Id));
        Assert.Equal(5, (await db.CatalogArtists.SingleAsync(a => a.Id == artist.Id)).DiscographyItemCount);
        Assert.NotNull((await db.CatalogArtists.SingleAsync(a => a.Id == artist.Id)).DiscographyFetchedAtUtc);

        var master = await db.CatalogMasters.SingleAsync(m => m.DiscogsId == 565);
        Assert.Equal(CatalogFetchState.Listed, master.FetchState);
        Assert.Equal("Selected Ambient Works 85-92", master.Title);
        Assert.Equal(1992, master.Year);
        Assert.False(string.IsNullOrWhiteSpace(master.Thumb));

        var shorter = page.Releases.Take(2).ToList();
        await ingestor.UpsertArtistDiscographyAsync(artist.Id, shorter, complete: true);

        Assert.Equal(2, await db.CatalogDiscographyEntries.CountAsync(e => e.OwnerId == artist.Id));
        Assert.Equal(2, (await db.CatalogArtists.SingleAsync(a => a.Id == artist.Id)).DiscographyItemCount);
    }

    [Fact]
    public async Task MarkUnavailable_Creates_Stub_And_Sets_NotFound_Or_Failed()
    {
        await using var db = CatalogIngestionTestKit.NewContext();
        var clock = CatalogIngestionTestKit.Clock();
        var ingestor = Create(db, clock);

        await ingestor.MarkUnavailableAsync(CatalogEntityKind.Release, 999, null);
        var missing = await db.CatalogReleases.SingleAsync(r => r.DiscogsId == 999);
        Assert.Equal(CatalogFetchState.NotFound, missing.FetchState);
        Assert.Null(missing.LastError);
        Assert.Equal(clock.Now.UtcDateTime.AddDays(30), missing.RefreshAfterUtc);

        await ingestor.MarkUnavailableAsync(CatalogEntityKind.Release, 999, "boom");
        missing = await db.CatalogReleases.SingleAsync(r => r.DiscogsId == 999);
        Assert.Equal(CatalogFetchState.Failed, missing.FetchState);
        Assert.Equal("boom", missing.LastError);
    }

    [Fact]
    public async Task Images_Retire_When_No_Longer_Present()
    {
        await using var db = CatalogIngestionTestKit.NewContext();
        var ingestor = Create(db);
        var doc = CatalogIngestionTestKit.Document<DiscogsArtist>("artist-45-aphex-twin.json");
        var artist = await ingestor.UpsertArtistAsync(doc);

        var slim = doc.Value with { Images = [doc.Value.Images![0]] };
        var slimDoc = new DiscogsDocument<DiscogsArtist>(slim, doc.RawJson + " ");
        await ingestor.UpsertArtistAsync(slimDoc);

        var images = await db.CatalogImages.Where(i => i.EntityKind == CatalogEntityKind.Artist && i.EntityId == artist.Id).ToListAsync();
        Assert.Contains(images, i => i.Status == CatalogImageStatus.Remote);
        Assert.Contains(images, i => i.Status == CatalogImageStatus.Retired);
    }

    private sealed record ArtistReleasesPage(IReadOnlyList<DiscogsArtistReleaseEntry> Releases);
}
