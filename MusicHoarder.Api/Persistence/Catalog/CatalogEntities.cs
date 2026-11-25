using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

// The catalog graph: artists, labels, masters (albums) and releases (editions) as Discogs describes
// them, with their tracklists, credits, labels and companies, formats, identifiers, styles, links and
// images. Catalog/reference data shared by every account, like CanonicalAlbum — no owner filter, and
// read only through admin endpoints. Nothing here points at a library row: a song names its release
// by SongMetadata.DiscogsReleaseId, so the catalog can be refreshed or purged without touching the
// library. The numbers of every enum below are stored; append only.

/// <summary>How much of a catalog resource has been read.</summary>
public enum CatalogFetchState
{
    /// <summary>Only named by another resource (a credit, a discography entry): name and id.</summary>
    Listed = 0,

    /// <summary>Read in full from its own resource.</summary>
    Fetched = 1,

    /// <summary>The source answered 404 for it.</summary>
    NotFound = 2,

    /// <summary>The last read failed; retried after <c>RefreshAfterUtc</c>.</summary>
    Failed = 3,
}

/// <summary>Which kind of catalog row a polymorphic row (image, link, external id, snapshot) belongs to.</summary>
public enum CatalogEntityKind
{
    Artist = 0,
    Label = 1,
    Master = 2,
    Release = 3,
}

/// <summary>Shared freshness bookkeeping of the four catalog resources.</summary>
public abstract class CatalogResource
{
    public int Id { get; set; }

    /// <summary>The Discogs id. Unique per resource kind.</summary>
    public long DiscogsId { get; set; }

    public CatalogFetchState FetchState { get; set; }

    [MaxLength(32)]
    public string? DataQuality { get; set; }

    [MaxLength(1024)]
    public string? DiscogsUri { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? FetchedAtUtc { get; set; }

    /// <summary>When the resource should be read again (stale data, or a failure's cooldown).</summary>
    public DateTime? RefreshAfterUtc { get; set; }

    [MaxLength(1024)]
    public string? LastError { get; set; }
}

public class CatalogArtist : CatalogResource
{
    [MaxLength(512)]
    public string Name { get; set; } = string.Empty;

    /// <summary><c>TitleNormalizer.NormalizeForSearch</c> of the name without the "(2)" disambiguation.</summary>
    [MaxLength(512)]
    public string NameKey { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? RealName { get; set; }

    public string? Profile { get; set; }

    /// <summary>When the artist's release listing was last read in full.</summary>
    public DateTime? DiscographyFetchedAtUtc { get; set; }

    public int DiscographyItemCount { get; set; }

    public List<CatalogArtistNameVariation> NameVariations { get; set; } = [];
    public List<CatalogArtistRelation> Relations { get; set; } = [];
}

public class CatalogArtistNameVariation
{
    public int Id { get; set; }
    public int ArtistId { get; set; }
    public CatalogArtist Artist { get; set; } = null!;
    public int Sequence { get; set; }

    [MaxLength(512)]
    public string Name { get; set; } = string.Empty;
}

public enum CatalogArtistRelationKind
{
    /// <summary>Another artist name the same person or group released under.</summary>
    Alias = 0,

    /// <summary>A member of this group.</summary>
    Member = 1,

    /// <summary>A group this artist belongs to.</summary>
    Group = 2,
}

/// <summary>An alias, member or group of an artist, by Discogs id (the related artist may not be read yet).</summary>
public class CatalogArtistRelation
{
    public int Id { get; set; }
    public int ArtistId { get; set; }
    public CatalogArtist Artist { get; set; } = null!;
    public CatalogArtistRelationKind Kind { get; set; }
    public int Sequence { get; set; }
    public long RelatedDiscogsId { get; set; }

    [MaxLength(512)]
    public string RelatedName { get; set; } = string.Empty;

    /// <summary>Discogs' <c>active</c> flag on a membership; null where it says nothing.</summary>
    public bool? Active { get; set; }
}

public class CatalogLabel : CatalogResource
{
    [MaxLength(512)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(512)]
    public string NameKey { get; set; } = string.Empty;

    public string? Profile { get; set; }

    public string? ContactInfo { get; set; }

    public long? ParentDiscogsId { get; set; }

    [MaxLength(512)]
    public string? ParentName { get; set; }

    /// <summary>When the label's release listing was last read in full.</summary>
    public DateTime? ReleasesFetchedAtUtc { get; set; }

    public int ReleaseItemCount { get; set; }

    public List<CatalogLabelSublabel> Sublabels { get; set; } = [];
}

public class CatalogLabelSublabel
{
    public int Id { get; set; }
    public int LabelId { get; set; }
    public CatalogLabel Label { get; set; } = null!;
    public int Sequence { get; set; }
    public long DiscogsId { get; set; }

    [MaxLength(512)]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// An album: what Discogs calls a master, grouping every edition of the same work. Kept apart from
/// <see cref="CatalogRelease"/> so country, year, format and catalog number of each edition survive.
/// </summary>
public class CatalogMaster : CatalogResource
{
    [MaxLength(512)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(512)]
    public string TitleKey { get; set; } = string.Empty;

    /// <summary>The artist credit as Discogs joins it ("Artist A & Artist B").</summary>
    [MaxLength(1024)]
    public string? ArtistDisplay { get; set; }

    [MaxLength(512)]
    public string? ArtistKey { get; set; }

    public int? Year { get; set; }

    public long? MainReleaseDiscogsId { get; set; }

    public long? MostRecentReleaseDiscogsId { get; set; }

    public string? Notes { get; set; }

    /// <summary>Discogs' broad genres, kept losslessly and never shown as the genre (styles are).</summary>
    public string? GenresJson { get; set; }

    [MaxLength(1024)]
    public string? Thumb { get; set; }

    /// <summary>When the master's versions (editions) were last listed in full.</summary>
    public DateTime? VersionsFetchedAtUtc { get; set; }

    public int VersionCount { get; set; }

    public List<CatalogRelease> Releases { get; set; } = [];
    public List<CatalogMasterStyle> Styles { get; set; } = [];
}

/// <summary>One edition: a specific pressing, format, country and catalog number.</summary>
public class CatalogRelease : CatalogResource
{
    public int? MasterId { get; set; }
    public CatalogMaster? Master { get; set; }

    /// <summary>The master Discogs names, kept even before that master is read.</summary>
    public long? MasterDiscogsId { get; set; }

    [MaxLength(512)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(512)]
    public string TitleKey { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string? ArtistDisplay { get; set; }

    [MaxLength(512)]
    public string? ArtistKey { get; set; }

    public int? Year { get; set; }

    [MaxLength(128)]
    public string? Country { get; set; }

    /// <summary>Discogs' release date as written ("1987-07-00", "1992").</summary>
    [MaxLength(32)]
    public string? Released { get; set; }

    [MaxLength(32)]
    public string? Status { get; set; }

    public string? Notes { get; set; }

    /// <summary>"CD, Album" / "12\", EP, Ltd" — the formats in one line, for lists and filters.</summary>
    [MaxLength(512)]
    public string? FormatSummary { get; set; }

    [MaxLength(512)]
    public string? LabelSummary { get; set; }

    [MaxLength(256)]
    public string? CatalogNumberSummary { get; set; }

    /// <summary>Discogs' broad genres, kept losslessly and never shown as the genre (styles are).</summary>
    public string? GenresJson { get; set; }

    [MaxLength(1024)]
    public string? Thumb { get; set; }

    public List<CatalogTrack> Tracks { get; set; } = [];
    public List<CatalogCredit> Credits { get; set; } = [];
    public List<CatalogReleaseLabel> Labels { get; set; } = [];
    public List<CatalogReleaseFormat> Formats { get; set; } = [];
    public List<CatalogReleaseIdentifier> Identifiers { get; set; } = [];
    public List<CatalogReleaseStyle> Styles { get; set; } = [];
}

/// <summary>A tracklist row of a release, in Discogs order: a track, a heading or an index track.</summary>
public class CatalogTrack
{
    public int Id { get; set; }
    public int ReleaseId { get; set; }
    public CatalogRelease Release { get; set; } = null!;
    public int Sequence { get; set; }

    /// <summary>The sequence of the index track this is a sub-track of; null at the top level.</summary>
    public int? ParentSequence { get; set; }

    [MaxLength(32)]
    public string? Position { get; set; }

    /// <summary>Discogs' <c>type_</c>: track, heading or index.</summary>
    [MaxLength(16)]
    public string Type { get; set; } = "track";

    [MaxLength(512)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(512)]
    public string TitleKey { get; set; } = string.Empty;

    [MaxLength(16)]
    public string? Duration { get; set; }

    public int? DurationSeconds { get; set; }

    /// <summary>Disc and track number read from the position ("2-03", "B1"), when it can be.</summary>
    public int? DiscNumber { get; set; }

    public int? TrackNumber { get; set; }
}

public enum CatalogCreditKind
{
    /// <summary>The release's (or track's) main artist credit.</summary>
    Main = 0,

    /// <summary>An extra artist: producer, remixer, design…, with its role.</summary>
    Extra = 1,
}

/// <summary>An artist credit on a release, or on one of its tracks when <see cref="TrackSequence"/> is set.</summary>
public class CatalogCredit
{
    public int Id { get; set; }
    public int ReleaseId { get; set; }
    public CatalogRelease Release { get; set; } = null!;
    public int Sequence { get; set; }
    public CatalogCreditKind Kind { get; set; }

    /// <summary>The <see cref="CatalogTrack.Sequence"/> the credit belongs to; null for the whole release.</summary>
    public int? TrackSequence { get; set; }

    public long ArtistDiscogsId { get; set; }

    [MaxLength(512)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Artist name variation printed on this release.</summary>
    [MaxLength(512)]
    public string? Anv { get; set; }

    [MaxLength(32)]
    public string? Join { get; set; }

    [MaxLength(512)]
    public string? Role { get; set; }

    /// <summary>Discogs' track range for a release-level extra credit ("A1, B2", "1 to 3").</summary>
    [MaxLength(256)]
    public string? Tracks { get; set; }
}

public enum CatalogReleaseLabelKind
{
    Label = 0,
    Company = 1,
    Series = 2,
}

/// <summary>A label (with its catalog number), a company (with its role) or a series a release names.</summary>
public class CatalogReleaseLabel
{
    public int Id { get; set; }
    public int ReleaseId { get; set; }
    public CatalogRelease Release { get; set; } = null!;
    public int Sequence { get; set; }
    public CatalogReleaseLabelKind Kind { get; set; }
    public long LabelDiscogsId { get; set; }

    [MaxLength(512)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? CatalogNumber { get; set; }

    [MaxLength(16)]
    public string? EntityType { get; set; }

    /// <summary>"Label", "Phonographic Copyright (p)", "Pressed By"…</summary>
    [MaxLength(128)]
    public string? EntityTypeName { get; set; }
}

public class CatalogReleaseFormat
{
    public int Id { get; set; }
    public int ReleaseId { get; set; }
    public CatalogRelease Release { get; set; } = null!;
    public int Sequence { get; set; }

    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(16)]
    public string? Quantity { get; set; }

    [MaxLength(512)]
    public string? Text { get; set; }

    /// <summary>JSON array of the format's descriptions ("Album", "Limited Edition", "33 ⅓ RPM").</summary>
    public string? DescriptionsJson { get; set; }
}

/// <summary>Barcode, matrix/runout, rights society, label code… as the release lists them.</summary>
public class CatalogReleaseIdentifier
{
    public int Id { get; set; }
    public int ReleaseId { get; set; }
    public CatalogRelease Release { get; set; } = null!;
    public int Sequence { get; set; }

    [MaxLength(64)]
    public string Type { get; set; } = string.Empty;

    [MaxLength(512)]
    public string Value { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? Description { get; set; }
}

/// <summary>A Discogs style ("Dub Techno"): the genre value the library shows.</summary>
public class CatalogStyle
{
    public int Id { get; set; }

    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(128)]
    public string NameKey { get; set; } = string.Empty;
}

public class CatalogReleaseStyle
{
    public int ReleaseId { get; set; }
    public CatalogRelease Release { get; set; } = null!;
    public int StyleId { get; set; }
    public CatalogStyle Style { get; set; } = null!;
    public int Sequence { get; set; }
}

public class CatalogMasterStyle
{
    public int MasterId { get; set; }
    public CatalogMaster Master { get; set; } = null!;
    public int StyleId { get; set; }
    public CatalogStyle Style { get; set; } = null!;
    public int Sequence { get; set; }
}

public enum CatalogDiscographyOwner
{
    Artist = 0,
    Label = 1,
}

/// <summary>
/// One line of an artist's or a label's catalog listing — a master or a release, possibly not read in
/// full yet — so a complete discography can be shown without one request per release.
/// </summary>
public class CatalogDiscographyEntry
{
    public int Id { get; set; }
    public CatalogDiscographyOwner OwnerKind { get; set; }

    /// <summary>The <see cref="CatalogArtist"/> or <see cref="CatalogLabel"/> id.</summary>
    public int OwnerId { get; set; }

    /// <summary>Master or Release: which of <see cref="MasterId"/> / <see cref="ReleaseId"/> is set.</summary>
    public CatalogEntityKind ItemKind { get; set; }

    /// <summary>The listed item's Discogs id (the uniqueness key; never null, unlike the two FKs).</summary>
    public long ItemDiscogsId { get; set; }

    public int? MasterId { get; set; }
    public CatalogMaster? Master { get; set; }
    public int? ReleaseId { get; set; }
    public CatalogRelease? Release { get; set; }

    /// <summary>The artist's role on it ("Main", "Remix", "Appearance", "TrackAppearance"); empty for labels.</summary>
    [MaxLength(64)]
    public string Role { get; set; } = string.Empty;

    /// <summary>The label's catalog number for it (label listings only).</summary>
    [MaxLength(256)]
    public string? CatalogNumber { get; set; }

    public int Sequence { get; set; }

    public DateTime SeenAtUtc { get; set; }
}

/// <summary>A link an artist, label or release lists (official site, Bandcamp, socials).</summary>
public class CatalogUrl
{
    public int Id { get; set; }
    public CatalogEntityKind EntityKind { get; set; }
    public int EntityId { get; set; }
    public int Sequence { get; set; }

    [MaxLength(2048)]
    public string Url { get; set; } = string.Empty;
}

public enum CatalogImageStatus
{
    /// <summary>Known from the source; not downloaded.</summary>
    Remote = 0,

    Downloaded = 1,
    Failed = 2,

    /// <summary>Superseded by a newer read that no longer lists it.</summary>
    Retired = 3,
}

/// <summary>
/// One image variant of a catalog resource as the source lists it, and its local copy once fetched.
/// Discogs serves signed URLs, so <see cref="SourceUrl"/> is stored as given and refreshed on every
/// read rather than built.
/// </summary>
public class CatalogImage
{
    public int Id { get; set; }
    public CatalogEntityKind EntityKind { get; set; }
    public int EntityId { get; set; }
    public int Sequence { get; set; }

    /// <summary>"primary" or "secondary".</summary>
    [MaxLength(32)]
    public string Type { get; set; } = "secondary";

    [MaxLength(2048)]
    public string SourceUrl { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string? ThumbnailUrl { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>SHA-256 (hex) of the downloaded bytes.</summary>
    [MaxLength(64)]
    public string? Checksum { get; set; }

    /// <summary>Path of the local copy, relative to <c>Discogs:ImageDirectory</c>.</summary>
    [MaxLength(512)]
    public string? LocalPath { get; set; }

    [MaxLength(64)]
    public string? ContentType { get; set; }

    public long? ByteSize { get; set; }

    public CatalogImageStatus Status { get; set; }

    public DateTime SeenAtUtc { get; set; }

    public DateTime? DownloadedAtUtc { get; set; }

    [MaxLength(1024)]
    public string? LastError { get; set; }
}

/// <summary>
/// An id another provider gives the same catalog thing — MusicBrainz, Deezer, Spotify, Apple Music —
/// so the old Deezer-keyed follows and links keep pointing at the right artist or album.
/// </summary>
public class CatalogExternalId
{
    public int Id { get; set; }
    public CatalogEntityKind EntityKind { get; set; }
    public int EntityId { get; set; }

    /// <summary>"musicbrainz", "deezer", "spotify", "applemusic", "bandcamp".</summary>
    [MaxLength(32)]
    public string Provider { get; set; } = string.Empty;

    [MaxLength(256)]
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>How the mapping is known: "musicbrainz-url-rel", "deezer-link", "manual".</summary>
    [MaxLength(64)]
    public string Source { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// The exact JSON a catalog resource was read from. One row per distinct payload (by hash), so the
/// normalized rows can be rebuilt — or a newly needed field exposed — without asking the source again.
/// </summary>
public class CatalogRawSnapshot
{
    public int Id { get; set; }

    /// <summary>"artist", "label", "master", "release".</summary>
    [MaxLength(32)]
    public string ResourceKind { get; set; } = string.Empty;

    public long DiscogsId { get; set; }

    /// <summary>SHA-256 (hex) of <see cref="PayloadJson"/>.</summary>
    [MaxLength(64)]
    public string PayloadHash { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>Version of the mapping that read it, bumped when the normalizer changes.</summary>
    public int MapperVersion { get; set; }

    public DateTime FetchedAtUtc { get; set; }
}
