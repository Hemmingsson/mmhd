using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>What a <see cref="CatalogIdentityDecision"/> is about. Stored; append only.</summary>
public enum CatalogIdentitySubject
{
    /// <summary>A library artist name (<c>SubjectKey</c> = its search key).</summary>
    LibraryArtist = 0,

    /// <summary>A library album (<c>SubjectKey</c> = artist key + U+001F + album key).</summary>
    LibraryAlbum = 1,
}

/// <summary>How a catalog identity was found. Stored; append only.</summary>
public enum CatalogIdentityMethod
{
    /// <summary>MusicBrainz' url relationship of the album's release / release group, or of the artist.</summary>
    MusicBrainzRelation = 0,

    /// <summary>A Discogs search confirmed by the release's barcode.</summary>
    Barcode = 1,

    /// <summary>A Discogs search confirmed by label + catalog number.</summary>
    CatalogNumber = 2,

    /// <summary>A Discogs search confirmed by the owner's own tracks being on the tracklist.</summary>
    Tracklist = 3,

    /// <summary>A Discogs search by name only — never accepted without a person.</summary>
    NameSearch = 4,

    /// <summary>Picked by a person.</summary>
    Manual = 5,

    /// <summary>Carried over from the songs' already-linked releases (an artist proven by its albums).</summary>
    LinkedReleases = 6,
}

public enum CatalogIdentityStatus
{
    /// <summary>Linked without a person: the evidence was at or above <c>Discogs:AutoAcceptConfidence</c>.</summary>
    AutoAccepted = 0,

    /// <summary>A possibility waiting for a person.</summary>
    Candidate = 1,

    /// <summary>Picked by a person.</summary>
    Chosen = 2,

    /// <summary>Turned down by a person; never offered again for the subject.</summary>
    Rejected = 3,

    /// <summary>Undone by rolling back the run that made it.</summary>
    Reverted = 4,

    /// <summary>Replaced by a later accepted or chosen decision for the same subject.</summary>
    Superseded = 5,
}

/// <summary>
/// One decision (or open candidate) linking a library artist or album to a catalog identity, with the
/// evidence behind it. Every automatic link is one of these rows, so each can be audited and undone,
/// and a person's choice is never overwritten by a later automatic pass. Owner-scoped: it is about
/// the owner's library.
/// </summary>
public class CatalogIdentityDecision
{
    public int Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public CatalogIdentitySubject SubjectKind { get; set; }

    [MaxLength(1100)]
    public string SubjectKey { get; set; } = string.Empty;

    /// <summary>The subject as the library spells it, for the review list.</summary>
    [MaxLength(1100)]
    public string SubjectDisplay { get; set; } = string.Empty;

    public CatalogEntityKind TargetKind { get; set; }
    public long DiscogsId { get; set; }

    [MaxLength(1024)]
    public string? TargetName { get; set; }

    public CatalogIdentityMethod Method { get; set; }
    public double Confidence { get; set; }
    public CatalogIdentityStatus Status { get; set; }

    /// <summary>JSON: what matched (ids, barcode, tracks found, MusicBrainz url).</summary>
    public string? EvidenceJson { get; set; }

    /// <summary>The backfill run that made it; null for a person's choice.</summary>
    public int? RunId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? DecidedAtUtc { get; set; }

    /// <summary>"system", or the id of the person who decided.</summary>
    [MaxLength(64)]
    public string? DecidedBy { get; set; }
}

public enum CatalogBackfillKind
{
    /// <summary>Link library albums to Discogs releases and fill the songs' Discogs ids.</summary>
    LibraryAlbums = 0,

    /// <summary>Link library artists and read their complete discographies.</summary>
    LibraryArtists = 1,

    /// <summary>Read linked releases (and their masters and labels) into the catalog.</summary>
    CatalogResources = 2,

    /// <summary>Download image variants.</summary>
    Images = 3,
}

public enum CatalogBackfillStatus
{
    Running = 0,

    /// <summary>Stopped at its cursor (rate limit, batch budget, shutdown); the next pass resumes it.</summary>
    Paused = 1,

    Completed = 2,
    Failed = 3,
    RolledBack = 4,
    Cancelled = 5,
}

/// <summary>
/// One resumable backfill pass. The cursor is the last subject it finished, so a stopped run picks up
/// after it; a dry run records the decisions it would make (status Candidate, never applied) and
/// changes nothing else. Rolling a run back reverts its decisions and the song changes it made
/// (recorded as <see cref="SongMetadataChange"/> rows whose Source names the run).
/// </summary>
public class CatalogBackfillRun
{
    public int Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public CatalogBackfillKind Kind { get; set; }
    public CatalogBackfillStatus Status { get; set; }
    public bool DryRun { get; set; }

    /// <summary>The last subject finished, as the run orders them (an album key, an artist key, an id).</summary>
    [MaxLength(1100)]
    public string? Cursor { get; set; }

    public int Processed { get; set; }
    public int Linked { get; set; }
    public int Candidates { get; set; }
    public int Unmatched { get; set; }
    public int Failed { get; set; }
    public int SongsUpdated { get; set; }
    public int RequestsUsed { get; set; }

    [MaxLength(1024)]
    public string? LastError { get; set; }

    [MaxLength(64)]
    public string? StartedBy { get; set; }

    public DateTime StartedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public DateTime? RolledBackAtUtc { get; set; }

    /// <summary>The <see cref="SongMetadataChange.Source"/> this run's song changes carry.</summary>
    public string ChangeSource => $"discogs-run:{Id}";
}
