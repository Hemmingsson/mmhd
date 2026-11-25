using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Catalog;

/// <summary>Shared InMemory catalog graph seeding for query / acquisition / endpoint tests.</summary>
internal static class CatalogTestKit
{
    public static Guid OwnerA => WellKnownUsers.OwnerId;
    public static Guid OwnerB => TestUsers.FriendId;

    public static MusicHoarderDbContext NewContext(Guid? filterUser = null)
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        if (filterUser is null)
            return new MusicHoarderDbContext(options);

        var user = filterUser == OwnerA
            ? TestCurrentUserAccessor.OwnerUser
            : new CurrentUser(filterUser.Value, "user@test.local", UserRole.Admin, "User");
        return new MusicHoarderDbContext(options, new TestCurrentUserAccessor(user));
    }

    public static MusicHoarderDbContext ContextOn(DbContextOptions options, Guid filterUser)
    {
        var user = filterUser == OwnerA
            ? TestCurrentUserAccessor.OwnerUser
            : filterUser == OwnerB
                ? TestCurrentUserAccessor.FriendUser
                : new CurrentUser(filterUser, "user@test.local", UserRole.Admin, "User");
        return new MusicHoarderDbContext(options, new TestCurrentUserAccessor(user));
    }

    public static DbContextOptions SharedOptions() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

    /// <summary>
    /// A Discogs-only release (no owned songs) with artist, label, master, tracks, styles, urls and images.
    /// </summary>
    public static CatalogGraph SeedDiscogsOnlyRelease(MusicHoarderDbContext db)
    {
        var now = DateTime.UtcNow;
        var style = new CatalogStyle { Name = "Dub Techno", NameKey = CatalogKeys.NameKey("Dub Techno") };
        db.CatalogStyles.Add(style);
        db.SaveChanges();

        var artist = new CatalogArtist
        {
            DiscogsId = 1001,
            Name = "Basic Channel",
            NameKey = CatalogKeys.NameKey("Basic Channel"),
            FetchState = CatalogFetchState.Fetched,
            CreatedAtUtc = now,
            FetchedAtUtc = now,
            Profile = "Berlin duo",
            DiscogsUri = "https://www.discogs.com/artist/1001",
        };
        artist.NameVariations.Add(new CatalogArtistNameVariation { Sequence = 0, Name = "BasicChannel" });
        artist.Relations.Add(new CatalogArtistRelation
        {
            Kind = CatalogArtistRelationKind.Alias, Sequence = 0, RelatedDiscogsId = 1002, RelatedName = "BC",
        });
        db.CatalogArtists.Add(artist);

        var label = new CatalogLabel
        {
            DiscogsId = 2001,
            Name = "Basic Channel",
            NameKey = CatalogKeys.NameKey("Basic Channel"),
            FetchState = CatalogFetchState.Fetched,
            CreatedAtUtc = now,
        };
        db.CatalogLabels.Add(label);

        var master = new CatalogMaster
        {
            DiscogsId = 3001,
            Title = "Lyot",
            TitleKey = CatalogKeys.TitleKey("Lyot"),
            ArtistDisplay = "Basic Channel",
            ArtistKey = CatalogKeys.NameKey("Basic Channel"),
            Year = 1993,
            FetchState = CatalogFetchState.Fetched,
            CreatedAtUtc = now,
            MainReleaseDiscogsId = 4001,
            VersionCount = 1,
            Thumb = "https://img.discogs.com/master-thumb.jpg",
        };
        db.CatalogMasters.Add(master);
        db.SaveChanges();

        var release = new CatalogRelease
        {
            DiscogsId = 4001,
            MasterId = master.Id,
            MasterDiscogsId = master.DiscogsId,
            Title = "Lyot Rmx",
            TitleKey = CatalogKeys.TitleKey("Lyot Rmx"),
            ArtistDisplay = "Basic Channel",
            ArtistKey = CatalogKeys.NameKey("Basic Channel"),
            Year = 1993,
            Country = "Germany",
            Released = "1993",
            Status = "Accepted",
            FetchState = CatalogFetchState.Fetched,
            CreatedAtUtc = now,
            FetchedAtUtc = now,
            FormatSummary = "12\", Vinyl",
            LabelSummary = "Basic Channel",
            CatalogNumberSummary = "BC-02",
            Thumb = "https://img.discogs.com/release-thumb.jpg",
            DiscogsUri = "https://www.discogs.com/release/4001",
        };
        db.CatalogReleases.Add(release);
        db.SaveChanges();

        master.MainReleaseDiscogsId = release.DiscogsId;
        db.CatalogMasterStyles.Add(new CatalogMasterStyle { MasterId = master.Id, StyleId = style.Id, Sequence = 0 });
        db.CatalogReleaseStyles.Add(new CatalogReleaseStyle { ReleaseId = release.Id, StyleId = style.Id, Sequence = 0 });

        db.CatalogTracks.AddRange(
            new CatalogTrack
            {
                ReleaseId = release.Id, Sequence = 1, Position = "A", Type = "track",
                Title = "Lyot (Reduce)", TitleKey = CatalogKeys.TitleKey("Lyot (Reduce)"),
                Duration = "7:00", DurationSeconds = 420, DiscNumber = 1, TrackNumber = 1,
            },
            new CatalogTrack
            {
                ReleaseId = release.Id, Sequence = 2, Position = "B", Type = "track",
                Title = "Lyot (Exduce)", TitleKey = CatalogKeys.TitleKey("Lyot (Exduce)"),
                Duration = "8:00", DurationSeconds = 480, DiscNumber = 1, TrackNumber = 2,
            });

        db.CatalogCredits.AddRange(
            new CatalogCredit
            {
                ReleaseId = release.Id, Sequence = 0, Kind = CatalogCreditKind.Main,
                ArtistDiscogsId = artist.DiscogsId, Name = "Basic Channel",
            },
            new CatalogCredit
            {
                ReleaseId = release.Id, Sequence = 1, Kind = CatalogCreditKind.Extra,
                ArtistDiscogsId = artist.DiscogsId, Name = "Basic Channel", Role = "Producer",
            },
            new CatalogCredit
            {
                ReleaseId = release.Id, Sequence = 2, Kind = CatalogCreditKind.Extra,
                TrackSequence = 1, ArtistDiscogsId = artist.DiscogsId, Name = "Basic Channel", Role = "Remix",
            });

        db.CatalogReleaseLabels.Add(new CatalogReleaseLabel
        {
            ReleaseId = release.Id, Sequence = 0, Kind = CatalogReleaseLabelKind.Label,
            LabelDiscogsId = label.DiscogsId, Name = label.Name, CatalogNumber = "BC-02",
            EntityTypeName = "Label",
        });
        db.CatalogReleaseFormats.Add(new CatalogReleaseFormat
        {
            ReleaseId = release.Id, Sequence = 0, Name = "Vinyl", Quantity = "1",
            DescriptionsJson = """["12\"","33 ⅓ RPM"]""",
        });
        db.CatalogReleaseIdentifiers.Add(new CatalogReleaseIdentifier
        {
            ReleaseId = release.Id, Sequence = 0, Type = "Barcode", Value = "1234567890123",
        });

        db.CatalogDiscographyEntries.Add(new CatalogDiscographyEntry
        {
            OwnerKind = CatalogDiscographyOwner.Artist,
            OwnerId = artist.Id,
            ItemKind = CatalogEntityKind.Release,
            ItemDiscogsId = release.DiscogsId,
            ReleaseId = release.Id,
            MasterId = master.Id,
            Role = "Main",
            Sequence = 0,
            SeenAtUtc = now,
        });
        db.CatalogDiscographyEntries.Add(new CatalogDiscographyEntry
        {
            OwnerKind = CatalogDiscographyOwner.Label,
            OwnerId = label.Id,
            ItemKind = CatalogEntityKind.Release,
            ItemDiscogsId = release.DiscogsId,
            ReleaseId = release.Id,
            CatalogNumber = "BC-02",
            Sequence = 0,
            SeenAtUtc = now,
        });

        db.CatalogUrls.AddRange(
            new CatalogUrl
            {
                EntityKind = CatalogEntityKind.Release, EntityId = release.Id, Sequence = 0,
                Url = "https://basicchannel.bandcamp.com/album/lyot",
            },
            new CatalogUrl
            {
                EntityKind = CatalogEntityKind.Release, EntityId = release.Id, Sequence = 1,
                Url = "https://open.spotify.com/album/abc",
            },
            new CatalogUrl
            {
                EntityKind = CatalogEntityKind.Artist, EntityId = artist.Id, Sequence = 0,
                Url = "https://www.basicchannel.com",
            });

        db.CatalogImages.Add(new CatalogImage
        {
            EntityKind = CatalogEntityKind.Release, EntityId = release.Id, Sequence = 0,
            Type = "primary",
            SourceUrl = "https://img.discogs.com/signed/primary.jpg",
            ThumbnailUrl = "https://img.discogs.com/signed/thumb.jpg",
            Width = 600, Height = 600,
            Status = CatalogImageStatus.Remote,
            SeenAtUtc = now,
        });

        db.SaveChanges();
        return new CatalogGraph(artist, label, master, release, style);
    }

    public static SongMetadata AddOwnedSong(
        MusicHoarderDbContext db, Guid owner, CatalogRelease release, string title,
        long? artistDiscogsId = null, long? masterDiscogsId = null)
    {
        var song = new SongMetadata
        {
            OwnerUserId = owner,
            SourcePath = $"/src/{owner:N}/{title}.flac",
            FileName = $"{title}.flac",
            Extension = ".flac",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Title = title,
            Artist = release.ArtistDisplay ?? "Artist",
            AlbumArtist = release.ArtistDisplay ?? "Artist",
            Album = release.Title,
            EnrichmentStatus = EnrichmentStatus.Matched,
            DiscogsReleaseId = release.DiscogsId,
            DiscogsMasterId = masterDiscogsId ?? release.MasterDiscogsId,
            DiscogsArtistId = artistDiscogsId,
        };
        db.Songs.Add(song);
        db.SaveChanges();
        return song;
    }

    public sealed record CatalogGraph(
        CatalogArtist Artist,
        CatalogLabel Label,
        CatalogMaster Master,
        CatalogRelease Release,
        CatalogStyle Style);
}
