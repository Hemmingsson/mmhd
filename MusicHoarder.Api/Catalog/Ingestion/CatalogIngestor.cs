using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Catalog.Ingestion;

/// <summary>Writes Discogs documents into the catalog graph. See <see cref="ICatalogIngestor"/>.</summary>
public sealed partial class CatalogIngestor(
    MusicHoarderDbContext db,
    TimeProvider time,
    IOptionsMonitor<DiscogsOptions> options) : ICatalogIngestor
{
    /// <summary>Bump when the mapping of a Discogs document into rows changes.</summary>
    public const int MapperVersion = 1;

    /// <summary>
    /// Every write goes through one process-wide lock: enrichment, the backfill and admin ingests can
    /// reach the same artist, label or style at once, and two scopes inserting the same Discogs id would
    /// hit the unique index. A failed write detaches only what it touched (see <see cref="ForgetFailedWrite"/>)
    /// so the caller's scope stays usable and keeps its own tracked rows.
    /// </summary>
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    private async Task<T> SerializedAsync<T>(Func<Task<T>> write, CancellationToken ct)
    {
        await WriteLock.WaitAsync(ct);
        try
        {
            var trackedBefore = db.ChangeTracker.Entries()
                .ToDictionary(e => e.Entity, e => e.State, ReferenceEqualityComparer.Instance);
            try
            {
                return await write();
            }
            catch
            {
                ForgetFailedWrite(trackedBefore);
                throw;
            }
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>
    /// Detaches every row the failed write added, loaded, edited or deleted, plus any row still pointing
    /// at one of those, so the caller's next SaveChanges cannot persist half an ingest. Rows tracked before
    /// the write that it did not edit keep their state (a pending caller change stays pending). Rows the
    /// write's own intermediate saves already stored are reloaded from the database on the next query.
    /// </summary>
    private void ForgetFailedWrite(Dictionary<object, EntityState> trackedBefore)
    {
        var tracker = db.ChangeTracker;
        var autoDetect = tracker.AutoDetectChangesEnabled;
        try
        {
            tracker.DetectChanges();
            tracker.AutoDetectChangesEnabled = false;

            var kept = new List<EntityEntry>();
            var dropped = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var entry in tracker.Entries().ToList())
            {
                if (TouchedByWrite(entry, trackedBefore))
                    dropped.Add(entry.Entity);
                else
                    kept.Add(entry);
            }

            bool grew;
            do
            {
                grew = false;
                for (var i = kept.Count - 1; i >= 0; i--)
                {
                    if (!PointsAtAny(kept[i], dropped)) continue;
                    dropped.Add(kept[i].Entity);
                    kept.RemoveAt(i);
                    grew = true;
                }
            } while (grew);

            foreach (var entity in dropped)
                db.Entry(entity).State = EntityState.Detached;
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = autoDetect;
        }
    }

    private static bool TouchedByWrite(EntityEntry entry, Dictionary<object, EntityState> trackedBefore)
    {
        if (!trackedBefore.TryGetValue(entry.Entity, out var before))
            return true;
        return entry.State switch
        {
            EntityState.Deleted => before != EntityState.Deleted,
            EntityState.Modified => before == EntityState.Unchanged,
            _ => false,
        };
    }

    private static bool PointsAtAny(EntityEntry entry, HashSet<object> dropped)
    {
        foreach (var navigation in entry.Navigations)
        {
            switch (navigation)
            {
                case ReferenceEntry { CurrentValue: { } target } when dropped.Contains(target):
                    return true;
                case CollectionEntry { CurrentValue: { } items }:
                    foreach (var item in items)
                    {
                        if (dropped.Contains(item)) return true;
                    }
                    break;
            }
        }

        return false;
    }

    public Task<CatalogArtist> UpsertArtistAsync(DiscogsDocument<DiscogsArtist> document, CancellationToken ct = default)
        => SerializedAsync(() => UpsertArtistCoreAsync(document, ct), ct);

    public Task<CatalogLabel> UpsertLabelAsync(DiscogsDocument<DiscogsLabel> document, CancellationToken ct = default)
        => SerializedAsync(() => UpsertLabelCoreAsync(document, ct), ct);

    public Task<CatalogMaster> UpsertMasterAsync(DiscogsDocument<DiscogsMaster> document, CancellationToken ct = default)
        => SerializedAsync(() => UpsertMasterCoreAsync(document, ct), ct);

    public Task<CatalogRelease> UpsertReleaseAsync(DiscogsDocument<DiscogsRelease> document, CancellationToken ct = default)
        => SerializedAsync(() => UpsertReleaseCoreAsync(document, ct), ct);

    public Task<int> UpsertArtistDiscographyAsync(
        int artistId, IReadOnlyList<DiscogsArtistReleaseEntry> entries, bool complete, CancellationToken ct = default)
        => SerializedAsync(() => UpsertArtistDiscographyCoreAsync(artistId, entries, complete, ct), ct);

    public Task<int> UpsertLabelReleasesAsync(
        int labelId, IReadOnlyList<DiscogsLabelReleaseEntry> entries, bool complete, CancellationToken ct = default)
        => SerializedAsync(() => UpsertLabelReleasesCoreAsync(labelId, entries, complete, ct), ct);

    public Task<int> UpsertMasterVersionsAsync(
        int masterId, IReadOnlyList<DiscogsMasterVersion> versions, bool complete, CancellationToken ct = default)
        => SerializedAsync(() => UpsertMasterVersionsCoreAsync(masterId, versions, complete, ct), ct);

    public Task MarkUnavailableAsync(CatalogEntityKind kind, long discogsId, string? error, CancellationToken ct = default)
        => SerializedAsync(async () => { await MarkUnavailableCoreAsync(kind, discogsId, error, ct); return true; }, ct);

    private async Task<CatalogArtist> UpsertArtistCoreAsync(DiscogsDocument<DiscogsArtist> document, CancellationToken ct = default)
    {
        var src = document.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var artist = await db.CatalogArtists
            .Include(a => a.NameVariations)
            .Include(a => a.Relations)
            .FirstOrDefaultAsync(a => a.DiscogsId == src.Id, ct);

        if (artist is null)
        {
            artist = new CatalogArtist { DiscogsId = src.Id, CreatedAtUtc = now };
            db.CatalogArtists.Add(artist);
        }

        artist.Name = CatalogKeys.StripDisambiguator(src.Name);
        artist.NameKey = CatalogKeys.NameKey(src.Name);
        artist.RealName = NullIfBlank(src.Realname);
        artist.Profile = NullIfBlank(src.Profile);
        MarkFetched(artist, src.DataQuality, src.Uri, now);

        ReplaceNameVariations(artist, src.Namevariations);
        ReplaceRelations(artist, src);
        await db.SaveChangesAsync(ct);

        await ReplaceUrlsAsync(CatalogEntityKind.Artist, artist.Id, src.Urls, ct);
        await ReplaceImagesAsync(CatalogEntityKind.Artist, artist.Id, src.Images, now, ct);
        await EnsureArtistStubsAsync(CollectRelatedArtists(src), now, ct);
        await MaybeInsertSnapshotAsync("artist", src.Id, document.RawJson, now, ct);
        await db.SaveChangesAsync(ct);
        return artist;
    }

    private async Task<CatalogLabel> UpsertLabelCoreAsync(DiscogsDocument<DiscogsLabel> document, CancellationToken ct = default)
    {
        var src = document.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var label = await db.CatalogLabels
            .Include(l => l.Sublabels)
            .FirstOrDefaultAsync(l => l.DiscogsId == src.Id, ct);

        if (label is null)
        {
            label = new CatalogLabel { DiscogsId = src.Id, CreatedAtUtc = now };
            db.CatalogLabels.Add(label);
        }

        label.Name = CatalogKeys.StripDisambiguator(src.Name);
        label.NameKey = CatalogKeys.NameKey(src.Name);
        label.Profile = NullIfBlank(src.Profile);
        label.ContactInfo = NullIfBlank(src.ContactInfo);
        label.ParentDiscogsId = src.ParentLabel?.Id;
        label.ParentName = NullIfBlank(src.ParentLabel?.Name);
        MarkFetched(label, src.DataQuality, src.Uri, now);

        ReplaceSublabels(label, src.Sublabels);
        await db.SaveChangesAsync(ct);

        await ReplaceUrlsAsync(CatalogEntityKind.Label, label.Id, src.Urls, ct);
        await ReplaceImagesAsync(CatalogEntityKind.Label, label.Id, src.Images, now, ct);

        var stubs = new List<(long Id, string Name)>();
        if (src.ParentLabel is { Id: > 0 } parent)
            stubs.Add((parent.Id, parent.Name ?? $"Label {parent.Id}"));
        if (src.Sublabels is not null)
        {
            foreach (var sub in src.Sublabels.Where(s => s.Id > 0))
                stubs.Add((sub.Id, sub.Name ?? $"Label {sub.Id}"));
        }

        await EnsureLabelStubsAsync(stubs, now, ct);
        await MaybeInsertSnapshotAsync("label", src.Id, document.RawJson, now, ct);
        await db.SaveChangesAsync(ct);
        return label;
    }

    private async Task<CatalogMaster> UpsertMasterCoreAsync(DiscogsDocument<DiscogsMaster> document, CancellationToken ct = default)
    {
        var src = document.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var master = await db.CatalogMasters
            .Include(m => m.Styles)
            .FirstOrDefaultAsync(m => m.DiscogsId == src.Id, ct);

        if (master is null)
        {
            master = new CatalogMaster { DiscogsId = src.Id, CreatedAtUtc = now };
            db.CatalogMasters.Add(master);
        }

        var artistDisplay = JoinArtists(src.Artists);
        master.Title = src.Title?.Trim() ?? string.Empty;
        master.TitleKey = CatalogKeys.TitleKey(src.Title);
        master.ArtistDisplay = artistDisplay;
        master.ArtistKey = CatalogKeys.NameKey(artistDisplay);
        master.Year = src.Year;
        master.MainReleaseDiscogsId = src.MainRelease;
        master.MostRecentReleaseDiscogsId = src.MostRecentRelease;
        master.Notes = NullIfBlank(src.Notes);
        master.GenresJson = SerializeJson(src.Genres);
        master.Thumb = FirstImageUri(src.Images);
        MarkFetched(master, src.DataQuality, src.Uri, now);

        await ReplaceStylesAsync(master, src.Styles, ct);
        await db.SaveChangesAsync(ct);

        await ReplaceImagesAsync(CatalogEntityKind.Master, master.Id, src.Images, now, ct);
        if (src.Artists is not null)
            await EnsureArtistStubsAsync(src.Artists.Where(a => a.Id > 0).Select(a => (a.Id, a.Name ?? $"Artist {a.Id}")), now, ct);
        if (src.MainRelease is long mainId and > 0)
            await EnsureReleaseStubAsync(mainId, src.Title, artistDisplay, src.Year, FirstImageUri(src.Images), null, null, null, now, ct);

        await MaybeInsertSnapshotAsync("master", src.Id, document.RawJson, now, ct);
        await db.SaveChangesAsync(ct);
        return master;
    }

    private async Task<CatalogRelease> UpsertReleaseCoreAsync(DiscogsDocument<DiscogsRelease> document, CancellationToken ct = default)
    {
        var src = document.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var release = await db.CatalogReleases
            .Include(r => r.Tracks)
            .Include(r => r.Credits)
            .Include(r => r.Labels)
            .Include(r => r.Formats)
            .Include(r => r.Identifiers)
            .Include(r => r.Styles)
            .FirstOrDefaultAsync(r => r.DiscogsId == src.Id, ct);

        if (release is null)
        {
            release = new CatalogRelease { DiscogsId = src.Id, CreatedAtUtc = now };
            db.CatalogReleases.Add(release);
        }

        var artistDisplay = NullIfBlank(src.ArtistsSort) ?? JoinArtists(src.Artists);
        release.Title = src.Title?.Trim() ?? string.Empty;
        release.TitleKey = CatalogKeys.TitleKey(src.Title);
        release.ArtistDisplay = artistDisplay;
        release.ArtistKey = CatalogKeys.NameKey(artistDisplay);
        release.Year = src.Year;
        release.Country = NullIfBlank(src.Country);
        release.Released = NullIfBlank(src.Released);
        release.Status = NullIfBlank(src.Status);
        release.Notes = NullIfBlank(src.Notes);
        release.GenresJson = SerializeJson(src.Genres);
        release.Thumb = NullIfBlank(src.Thumb) ?? FirstImageUri(src.Images);
        release.FormatSummary = BuildFormatSummary(src.Formats);
        release.LabelSummary = BuildLabelSummary(src.Labels);
        release.CatalogNumberSummary = BuildCatalogNumberSummary(src.Labels);
        release.MasterDiscogsId = src.MasterId;
        MarkFetched(release, src.DataQuality, src.Uri, now);

        if (src.MasterId is long masterDiscogsId and > 0)
        {
            var master = await EnsureMasterStubAsync(masterDiscogsId, src.Title, artistDisplay, src.Year, release.Thumb, now, ct);
            release.MasterId = master.Id;
        }
        else
        {
            release.MasterId = null;
        }

        ReplaceTracks(release, src.Tracklist);
        ReplaceCredits(release, src);
        ReplaceReleaseLabels(release, src);
        ReplaceFormats(release, src.Formats);
        ReplaceIdentifiers(release, src.Identifiers);
        await ReplaceReleaseStylesAsync(release, src.Styles, ct);
        await db.SaveChangesAsync(ct);

        await ReplaceImagesAsync(CatalogEntityKind.Release, release.Id, src.Images, now, ct);

        var artistStubs = new List<(long Id, string Name)>();
        CollectCreditArtists(src.Artists, artistStubs);
        CollectCreditArtists(src.Extraartists, artistStubs);
        if (src.Tracklist is not null)
        {
            foreach (var track in FlattenTracks(src.Tracklist))
            {
                CollectCreditArtists(track.Artists, artistStubs);
                CollectCreditArtists(track.Extraartists, artistStubs);
            }
        }

        await EnsureArtistStubsAsync(artistStubs, now, ct);

        var labelStubs = new List<(long Id, string Name)>();
        CollectLabelRefs(src.Labels, labelStubs);
        CollectLabelRefs(src.Companies, labelStubs);
        CollectLabelRefs(src.Series, labelStubs);
        await EnsureLabelStubsAsync(labelStubs, now, ct);

        await MaybeInsertSnapshotAsync("release", src.Id, document.RawJson, now, ct);
        await db.SaveChangesAsync(ct);
        return release;
    }

    private async Task<int> UpsertArtistDiscographyCoreAsync(
        int artistId, IReadOnlyList<DiscogsArtistReleaseEntry> entries, bool complete, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var artist = await db.CatalogArtists.FirstOrDefaultAsync(a => a.Id == artistId, ct)
            ?? throw new InvalidOperationException($"Catalog artist {artistId} not found.");

        var existing = await db.CatalogDiscographyEntries
            .Where(e => e.OwnerKind == CatalogDiscographyOwner.Artist && e.OwnerId == artistId)
            .ToListAsync(ct);

        var kept = new HashSet<(CatalogEntityKind Kind, long Id, string Role)>();
        var sequence = 0;
        foreach (var entry in entries)
        {
            var kind = string.Equals(entry.Type, "master", StringComparison.OrdinalIgnoreCase)
                ? CatalogEntityKind.Master
                : CatalogEntityKind.Release;
            var role = entry.Role?.Trim() ?? string.Empty;
            kept.Add((kind, entry.Id, role));

            CatalogDiscographyEntry row;
            var match = existing.FirstOrDefault(e =>
                e.ItemKind == kind && e.ItemDiscogsId == entry.Id && e.Role == role);
            if (match is null)
            {
                row = new CatalogDiscographyEntry
                {
                    OwnerKind = CatalogDiscographyOwner.Artist,
                    OwnerId = artistId,
                    ItemKind = kind,
                    ItemDiscogsId = entry.Id,
                    Role = role,
                };
                db.CatalogDiscographyEntries.Add(row);
                existing.Add(row);
            }
            else
            {
                row = match;
            }

            row.Sequence = sequence++;
            row.SeenAtUtc = now;

            if (kind == CatalogEntityKind.Master)
            {
                var master = await EnsureMasterStubAsync(
                    entry.Id, entry.Title, entry.Artist, entry.Year, entry.Thumb, now, ct);
                row.MasterId = master.Id;
                row.ReleaseId = null;
                if (entry.MainRelease is long main and > 0)
                    await EnsureReleaseStubAsync(main, entry.Title, entry.Artist, entry.Year, entry.Thumb, entry.Format, entry.Label, null, now, ct);
            }
            else
            {
                var release = await EnsureReleaseStubAsync(
                    entry.Id, entry.Title, entry.Artist, entry.Year, entry.Thumb, entry.Format, entry.Label, null, now, ct);
                row.ReleaseId = release.Id;
                row.MasterId = null;
            }
        }

        if (complete)
        {
            foreach (var stale in existing.Where(e => !kept.Contains((e.ItemKind, e.ItemDiscogsId, e.Role))).ToList())
                db.CatalogDiscographyEntries.Remove(stale);
            artist.DiscographyFetchedAtUtc = now;
            artist.DiscographyItemCount = kept.Count;
        }

        await db.SaveChangesAsync(ct);
        return kept.Count;
    }

    private async Task<int> UpsertLabelReleasesCoreAsync(
        int labelId, IReadOnlyList<DiscogsLabelReleaseEntry> entries, bool complete, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var label = await db.CatalogLabels.FirstOrDefaultAsync(l => l.Id == labelId, ct)
            ?? throw new InvalidOperationException($"Catalog label {labelId} not found.");

        var existing = await db.CatalogDiscographyEntries
            .Where(e => e.OwnerKind == CatalogDiscographyOwner.Label && e.OwnerId == labelId)
            .ToListAsync(ct);

        var kept = new HashSet<(CatalogEntityKind Kind, long Id, string Role)>();
        var sequence = 0;
        foreach (var entry in entries)
        {
            const CatalogEntityKind kind = CatalogEntityKind.Release;
            const string role = "";
            kept.Add((kind, entry.Id, role));

            CatalogDiscographyEntry row;
            var match = existing.FirstOrDefault(e =>
                e.ItemKind == kind && e.ItemDiscogsId == entry.Id && e.Role == role);
            if (match is null)
            {
                row = new CatalogDiscographyEntry
                {
                    OwnerKind = CatalogDiscographyOwner.Label,
                    OwnerId = labelId,
                    ItemKind = kind,
                    ItemDiscogsId = entry.Id,
                    Role = role,
                };
                db.CatalogDiscographyEntries.Add(row);
                existing.Add(row);
            }
            else
            {
                row = match;
            }

            row.Sequence = sequence++;
            row.SeenAtUtc = now;
            row.CatalogNumber = NullIfBlank(entry.Catno);

            var release = await EnsureReleaseStubAsync(
                entry.Id, entry.Title, entry.Artist, entry.Year, entry.Thumb, entry.Format, label.Name, entry.Catno, now, ct);
            row.ReleaseId = release.Id;
            row.MasterId = null;
        }

        if (complete)
        {
            foreach (var stale in existing.Where(e => !kept.Contains((e.ItemKind, e.ItemDiscogsId, e.Role))).ToList())
                db.CatalogDiscographyEntries.Remove(stale);
            label.ReleasesFetchedAtUtc = now;
            label.ReleaseItemCount = kept.Count;
        }

        await db.SaveChangesAsync(ct);
        return kept.Count;
    }

    private async Task<int> UpsertMasterVersionsCoreAsync(
        int masterId, IReadOnlyList<DiscogsMasterVersion> versions, bool complete, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var master = await db.CatalogMasters.FirstOrDefaultAsync(m => m.Id == masterId, ct)
            ?? throw new InvalidOperationException($"Catalog master {masterId} not found.");

        // Versions are release stubs under the master; stored as discography-style rows is not used —
        // the listing stamps VersionsFetchedAtUtc / VersionCount and creates Listed release stubs.
        // Keep a DiscographyOwner.Artist-style uniqueness isn't applicable; versions only update master meta.
        _ = complete;

        var sequence = 0;
        foreach (var version in versions)
        {
            var year = ParseYear(version.Released) ?? master.Year;
            await EnsureReleaseStubAsync(
                version.Id,
                version.Title ?? master.Title,
                master.ArtistDisplay,
                year,
                version.Thumb,
                version.Format,
                version.Label,
                version.Catno,
                now,
                ct,
                master.Id,
                master.DiscogsId,
                version.Country,
                version.Status);
            sequence++;
        }

        if (complete)
        {
            master.VersionsFetchedAtUtc = now;
            master.VersionCount = sequence;
        }

        await db.SaveChangesAsync(ct);
        return sequence;
    }

    private async Task MarkUnavailableCoreAsync(CatalogEntityKind kind, long discogsId, string? error, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var refreshAfter = now.AddDays(Math.Max(1, options.CurrentValue.RefreshDays));
        var notFound = string.IsNullOrWhiteSpace(error);

        switch (kind)
        {
            case CatalogEntityKind.Artist:
            {
                var row = await db.CatalogArtists.FirstOrDefaultAsync(a => a.DiscogsId == discogsId, ct);
                if (row is null)
                {
                    row = new CatalogArtist
                    {
                        DiscogsId = discogsId,
                        Name = $"Artist {discogsId}",
                        NameKey = CatalogKeys.NameKey($"Artist {discogsId}"),
                        CreatedAtUtc = now,
                    };
                    db.CatalogArtists.Add(row);
                }

                ApplyUnavailable(row, notFound, error, refreshAfter);
                break;
            }
            case CatalogEntityKind.Label:
            {
                var row = await db.CatalogLabels.FirstOrDefaultAsync(l => l.DiscogsId == discogsId, ct);
                if (row is null)
                {
                    row = new CatalogLabel
                    {
                        DiscogsId = discogsId,
                        Name = $"Label {discogsId}",
                        NameKey = CatalogKeys.NameKey($"Label {discogsId}"),
                        CreatedAtUtc = now,
                    };
                    db.CatalogLabels.Add(row);
                }

                ApplyUnavailable(row, notFound, error, refreshAfter);
                break;
            }
            case CatalogEntityKind.Master:
            {
                var row = await db.CatalogMasters.FirstOrDefaultAsync(m => m.DiscogsId == discogsId, ct);
                if (row is null)
                {
                    row = new CatalogMaster
                    {
                        DiscogsId = discogsId,
                        Title = $"Master {discogsId}",
                        TitleKey = CatalogKeys.TitleKey($"Master {discogsId}"),
                        CreatedAtUtc = now,
                    };
                    db.CatalogMasters.Add(row);
                }

                ApplyUnavailable(row, notFound, error, refreshAfter);
                break;
            }
            case CatalogEntityKind.Release:
            {
                var row = await db.CatalogReleases.FirstOrDefaultAsync(r => r.DiscogsId == discogsId, ct);
                if (row is null)
                {
                    row = new CatalogRelease
                    {
                        DiscogsId = discogsId,
                        Title = $"Release {discogsId}",
                        TitleKey = CatalogKeys.TitleKey($"Release {discogsId}"),
                        CreatedAtUtc = now,
                    };
                    db.CatalogReleases.Add(row);
                }

                ApplyUnavailable(row, notFound, error, refreshAfter);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        await db.SaveChangesAsync(ct);
    }

    private static void ApplyUnavailable(CatalogResource row, bool notFound, string? error, DateTime refreshAfter)
    {
        row.FetchState = notFound ? CatalogFetchState.NotFound : CatalogFetchState.Failed;
        row.LastError = notFound ? null : Truncate(error, 1024);
        row.RefreshAfterUtc = refreshAfter;
    }

    private void MarkFetched(CatalogResource row, string? dataQuality, string? uri, DateTime now)
    {
        row.FetchState = CatalogFetchState.Fetched;
        row.FetchedAtUtc = now;
        row.RefreshAfterUtc = now.AddDays(Math.Max(1, options.CurrentValue.RefreshDays));
        row.DataQuality = NullIfBlank(dataQuality);
        row.DiscogsUri = NullIfBlank(uri);
        row.LastError = null;
    }

    private async Task MaybeInsertSnapshotAsync(string resourceKind, long discogsId, string rawJson, DateTime now, CancellationToken ct)
    {
        var hash = Sha256Hex(rawJson);
        var latest = await db.CatalogRawSnapshots
            .Where(s => s.ResourceKind == resourceKind && s.DiscogsId == discogsId)
            .OrderByDescending(s => s.FetchedAtUtc)
            .ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);

        if (latest is not null && latest.PayloadHash == hash)
            return;

        db.CatalogRawSnapshots.Add(new CatalogRawSnapshot
        {
            ResourceKind = resourceKind,
            DiscogsId = discogsId,
            PayloadHash = hash,
            PayloadJson = rawJson,
            MapperVersion = MapperVersion,
            FetchedAtUtc = now,
        });
    }

    private static void ReplaceNameVariations(CatalogArtist artist, IReadOnlyList<string>? variations)
    {
        artist.NameVariations.Clear();
        if (variations is null) return;
        var seq = 0;
        foreach (var name in variations)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            artist.NameVariations.Add(new CatalogArtistNameVariation
            {
                Sequence = seq++,
                Name = name.Trim(),
            });
        }
    }

    private static void ReplaceRelations(CatalogArtist artist, DiscogsArtist src)
    {
        artist.Relations.Clear();
        AddRelations(artist, CatalogArtistRelationKind.Alias, src.Aliases);
        AddRelations(artist, CatalogArtistRelationKind.Member, src.Members);
        AddRelations(artist, CatalogArtistRelationKind.Group, src.Groups);
    }

    private static void AddRelations(CatalogArtist artist, CatalogArtistRelationKind kind, IReadOnlyList<DiscogsArtistRef>? refs)
    {
        if (refs is null) return;
        var seq = 0;
        foreach (var r in refs)
        {
            if (r.Id <= 0) continue;
            artist.Relations.Add(new CatalogArtistRelation
            {
                Kind = kind,
                Sequence = seq++,
                RelatedDiscogsId = r.Id,
                RelatedName = CatalogKeys.StripDisambiguator(r.Name) is { Length: > 0 } n ? n : $"Artist {r.Id}",
                Active = r.Active,
            });
        }
    }

    private static void ReplaceSublabels(CatalogLabel label, IReadOnlyList<DiscogsLabelRef>? sublabels)
    {
        label.Sublabels.Clear();
        if (sublabels is null) return;
        var seq = 0;
        foreach (var sub in sublabels)
        {
            if (sub.Id <= 0) continue;
            label.Sublabels.Add(new CatalogLabelSublabel
            {
                Sequence = seq++,
                DiscogsId = sub.Id,
                Name = CatalogKeys.StripDisambiguator(sub.Name) is { Length: > 0 } n ? n : $"Label {sub.Id}",
            });
        }
    }

    private void ReplaceTracks(CatalogRelease release, IReadOnlyList<DiscogsTrack>? tracklist)
    {
        release.Tracks.Clear();
        if (tracklist is null) return;

        var sequence = 0;
        void AddTrack(DiscogsTrack track, int? parentSequence)
        {
            var type = string.IsNullOrWhiteSpace(track.Type) ? "track" : track.Type!;
            var title = track.Title?.Trim() ?? string.Empty;
            var (disc, trackNo) = ParsePosition(track.Position);
            release.Tracks.Add(new CatalogTrack
            {
                Sequence = sequence,
                ParentSequence = parentSequence,
                Position = NullIfBlank(track.Position),
                Type = type,
                Title = title,
                TitleKey = CatalogKeys.TitleKey(title),
                Duration = NullIfBlank(track.Duration),
                DurationSeconds = ParseDurationSeconds(track.Duration),
                DiscNumber = disc,
                TrackNumber = trackNo,
            });
            var ownSequence = sequence++;
            if (track.SubTracks is null) return;
            foreach (var sub in track.SubTracks)
                AddTrack(sub, ownSequence);
        }

        foreach (var track in tracklist)
            AddTrack(track, null);
    }

    private static void ReplaceCredits(CatalogRelease release, DiscogsRelease src)
    {
        release.Credits.Clear();
        var sequence = 0;

        void AddList(IReadOnlyList<DiscogsArtistRef>? list, CatalogCreditKind kind, int? trackSequence)
        {
            if (list is null) return;
            foreach (var a in list)
            {
                if (a.Id <= 0) continue;
                release.Credits.Add(new CatalogCredit
                {
                    Sequence = sequence++,
                    Kind = kind,
                    TrackSequence = trackSequence,
                    ArtistDiscogsId = a.Id,
                    Name = CatalogKeys.StripDisambiguator(a.Name) is { Length: > 0 } n ? n : $"Artist {a.Id}",
                    Anv = NullIfBlank(a.Anv),
                    Join = NullIfBlank(a.Join),
                    Role = NullIfBlank(a.Role),
                    Tracks = NullIfBlank(a.Tracks),
                });
            }
        }

        AddList(src.Artists, CatalogCreditKind.Main, null);
        AddList(src.Extraartists, CatalogCreditKind.Extra, null);

        if (src.Tracklist is null) return;
        var flatSeq = 0;
        void Walk(DiscogsTrack track)
        {
            var trackSequence = flatSeq++;
            AddList(track.Artists, CatalogCreditKind.Main, trackSequence);
            AddList(track.Extraartists, CatalogCreditKind.Extra, trackSequence);
            if (track.SubTracks is null) return;
            foreach (var sub in track.SubTracks)
                Walk(sub);
        }

        foreach (var track in src.Tracklist)
            Walk(track);
    }

    private static void ReplaceReleaseLabels(CatalogRelease release, DiscogsRelease src)
    {
        release.Labels.Clear();
        var sequence = 0;

        void Add(IReadOnlyList<DiscogsLabelRef>? list, CatalogReleaseLabelKind kind)
        {
            if (list is null) return;
            foreach (var item in list)
            {
                if (item.Id <= 0) continue;
                release.Labels.Add(new CatalogReleaseLabel
                {
                    Sequence = sequence++,
                    Kind = kind,
                    LabelDiscogsId = item.Id,
                    Name = CatalogKeys.StripDisambiguator(item.Name) is { Length: > 0 } n ? n : $"Label {item.Id}",
                    CatalogNumber = NullIfBlank(item.Catno),
                    EntityType = NullIfBlank(item.EntityType),
                    EntityTypeName = NullIfBlank(item.EntityTypeName),
                });
            }
        }

        Add(src.Labels, CatalogReleaseLabelKind.Label);
        Add(src.Companies, CatalogReleaseLabelKind.Company);
        Add(src.Series, CatalogReleaseLabelKind.Series);
    }

    private static void ReplaceFormats(CatalogRelease release, IReadOnlyList<DiscogsFormat>? formats)
    {
        release.Formats.Clear();
        if (formats is null) return;
        var sequence = 0;
        foreach (var format in formats)
        {
            release.Formats.Add(new CatalogReleaseFormat
            {
                Sequence = sequence++,
                Name = format.Name?.Trim() ?? string.Empty,
                Quantity = NullIfBlank(format.Qty),
                Text = NullIfBlank(format.Text),
                DescriptionsJson = SerializeJson(format.Descriptions),
            });
        }
    }

    private static void ReplaceIdentifiers(CatalogRelease release, IReadOnlyList<DiscogsIdentifier>? identifiers)
    {
        release.Identifiers.Clear();
        if (identifiers is null) return;
        var sequence = 0;
        foreach (var id in identifiers)
        {
            // Keep raw rows (including barcode "none"); callers use CatalogKeys.IsUsableBarcode for UPC use.
            release.Identifiers.Add(new CatalogReleaseIdentifier
            {
                Sequence = sequence++,
                Type = id.Type?.Trim() ?? string.Empty,
                Value = id.Value?.Trim() ?? string.Empty,
                Description = NullIfBlank(id.Description),
            });
        }
    }

    private async Task ReplaceStylesAsync(CatalogMaster master, IReadOnlyList<string>? styles, CancellationToken ct)
    {
        master.Styles.Clear();
        if (styles is null) return;
        var sequence = 0;
        foreach (var name in styles)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var style = await EnsureStyleAsync(name, ct);
            master.Styles.Add(new CatalogMasterStyle { StyleId = style.Id, Sequence = sequence++, Style = style });
        }
    }

    private async Task ReplaceReleaseStylesAsync(CatalogRelease release, IReadOnlyList<string>? styles, CancellationToken ct)
    {
        release.Styles.Clear();
        if (styles is null) return;
        var sequence = 0;
        foreach (var name in styles)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var style = await EnsureStyleAsync(name, ct);
            release.Styles.Add(new CatalogReleaseStyle { StyleId = style.Id, Sequence = sequence++, Style = style });
        }
    }

    private async Task<CatalogStyle> EnsureStyleAsync(string name, CancellationToken ct)
    {
        var key = CatalogKeys.NameKey(name);
        var existing = await db.CatalogStyles.FirstOrDefaultAsync(s => s.NameKey == key, ct);
        if (existing is not null) return existing;

        var style = new CatalogStyle { Name = name.Trim(), NameKey = key };
        db.CatalogStyles.Add(style);
        await db.SaveChangesAsync(ct);
        return style;
    }

    private async Task ReplaceUrlsAsync(CatalogEntityKind kind, int entityId, IReadOnlyList<string>? urls, CancellationToken ct)
    {
        var existing = await db.CatalogUrls.Where(u => u.EntityKind == kind && u.EntityId == entityId).ToListAsync(ct);
        db.CatalogUrls.RemoveRange(existing);
        if (urls is null) return;
        var sequence = 0;
        foreach (var url in urls)
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            db.CatalogUrls.Add(new CatalogUrl
            {
                EntityKind = kind,
                EntityId = entityId,
                Sequence = sequence++,
                Url = url.Trim(),
            });
        }
    }

    private async Task ReplaceImagesAsync(
        CatalogEntityKind kind, int entityId, IReadOnlyList<DiscogsImage>? images, DateTime now, CancellationToken ct)
    {
        var existing = await db.CatalogImages.Where(i => i.EntityKind == kind && i.EntityId == entityId).ToListAsync(ct);
        var byKey = existing.ToDictionary(i => CatalogKeys.StableImageKey(i.SourceUrl), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sequence = 0;

        if (images is not null)
        {
            foreach (var image in images)
            {
                var source = NullIfBlank(image.Uri) ?? NullIfBlank(image.ResourceUrl);
                if (source is null) continue;
                var key = CatalogKeys.StableImageKey(source);
                if (key.Length == 0 || !seen.Add(key)) continue;

                if (byKey.TryGetValue(key, out var row))
                {
                    row.Sequence = sequence++;
                    row.Type = string.IsNullOrWhiteSpace(image.Type) ? "secondary" : image.Type!;
                    row.SourceUrl = source;
                    row.ThumbnailUrl = NullIfBlank(image.Uri150);
                    row.Width = image.Width;
                    row.Height = image.Height;
                    row.SeenAtUtc = now;
                    if (row.Status == CatalogImageStatus.Retired)
                        row.Status = CatalogImageStatus.Remote;
                }
                else
                {
                    db.CatalogImages.Add(new CatalogImage
                    {
                        EntityKind = kind,
                        EntityId = entityId,
                        Sequence = sequence++,
                        Type = string.IsNullOrWhiteSpace(image.Type) ? "secondary" : image.Type!,
                        SourceUrl = source,
                        ThumbnailUrl = NullIfBlank(image.Uri150),
                        Width = image.Width,
                        Height = image.Height,
                        Status = CatalogImageStatus.Remote,
                        SeenAtUtc = now,
                    });
                }
            }
        }

        foreach (var row in existing)
        {
            var key = CatalogKeys.StableImageKey(row.SourceUrl);
            if (!seen.Contains(key) && row.Status != CatalogImageStatus.Retired)
                row.Status = CatalogImageStatus.Retired;
        }
    }

    private async Task EnsureArtistStubsAsync(IEnumerable<(long Id, string Name)> artists, DateTime now, CancellationToken ct)
    {
        foreach (var (id, name) in artists.DistinctBy(a => a.Id))
        {
            if (id <= 0) continue;
            var existing = await db.CatalogArtists.FirstOrDefaultAsync(a => a.DiscogsId == id, ct);
            if (existing is not null) continue;
            var display = CatalogKeys.StripDisambiguator(name);
            if (display.Length == 0) display = $"Artist {id}";
            db.CatalogArtists.Add(new CatalogArtist
            {
                DiscogsId = id,
                Name = display,
                NameKey = CatalogKeys.NameKey(display),
                FetchState = CatalogFetchState.Listed,
                CreatedAtUtc = now,
            });
        }
    }

    private async Task EnsureLabelStubsAsync(IEnumerable<(long Id, string Name)> labels, DateTime now, CancellationToken ct)
    {
        foreach (var (id, name) in labels.DistinctBy(l => l.Id))
        {
            if (id <= 0) continue;
            var existing = await db.CatalogLabels.FirstOrDefaultAsync(l => l.DiscogsId == id, ct);
            if (existing is not null) continue;
            var display = CatalogKeys.StripDisambiguator(name);
            if (display.Length == 0) display = $"Label {id}";
            db.CatalogLabels.Add(new CatalogLabel
            {
                DiscogsId = id,
                Name = display,
                NameKey = CatalogKeys.NameKey(display),
                FetchState = CatalogFetchState.Listed,
                CreatedAtUtc = now,
            });
        }
    }

    private async Task<CatalogMaster> EnsureMasterStubAsync(
        long discogsId, string? title, string? artistDisplay, int? year, string? thumb, DateTime now, CancellationToken ct)
    {
        var existing = await db.CatalogMasters.FirstOrDefaultAsync(m => m.DiscogsId == discogsId, ct);
        if (existing is not null)
        {
            if (existing.FetchState == CatalogFetchState.Listed)
                ApplyListingFields(existing, title, artistDisplay, year, thumb);
            return existing;
        }

        var master = new CatalogMaster
        {
            DiscogsId = discogsId,
            FetchState = CatalogFetchState.Listed,
            CreatedAtUtc = now,
        };
        ApplyListingFields(master, title, artistDisplay, year, thumb);
        db.CatalogMasters.Add(master);
        await db.SaveChangesAsync(ct);
        return master;
    }

    private async Task<CatalogRelease> EnsureReleaseStubAsync(
        long discogsId,
        string? title,
        string? artistDisplay,
        int? year,
        string? thumb,
        string? format,
        string? label,
        string? catno,
        DateTime now,
        CancellationToken ct,
        int? masterId = null,
        long? masterDiscogsId = null,
        string? country = null,
        string? status = null)
    {
        var existing = await db.CatalogReleases.FirstOrDefaultAsync(r => r.DiscogsId == discogsId, ct);
        if (existing is not null)
        {
            if (existing.FetchState == CatalogFetchState.Listed)
            {
                ApplyListingFields(existing, title, artistDisplay, year, thumb, format, label, catno);
                if (masterId is int mid) existing.MasterId = mid;
                if (masterDiscogsId is long md) existing.MasterDiscogsId = md;
                if (!string.IsNullOrWhiteSpace(country)) existing.Country = country.Trim();
                if (!string.IsNullOrWhiteSpace(status)) existing.Status = status.Trim();
            }

            return existing;
        }

        var release = new CatalogRelease
        {
            DiscogsId = discogsId,
            FetchState = CatalogFetchState.Listed,
            CreatedAtUtc = now,
            MasterId = masterId,
            MasterDiscogsId = masterDiscogsId,
            Country = NullIfBlank(country),
            Status = NullIfBlank(status),
        };
        ApplyListingFields(release, title, artistDisplay, year, thumb, format, label, catno);
        db.CatalogReleases.Add(release);
        await db.SaveChangesAsync(ct);
        return release;
    }

    private static void ApplyListingFields(CatalogMaster master, string? title, string? artistDisplay, int? year, string? thumb)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            master.Title = title.Trim();
            master.TitleKey = CatalogKeys.TitleKey(title);
        }
        else if (string.IsNullOrEmpty(master.Title))
        {
            master.Title = $"Master {master.DiscogsId}";
            master.TitleKey = CatalogKeys.TitleKey(master.Title);
        }

        if (!string.IsNullOrWhiteSpace(artistDisplay))
        {
            master.ArtistDisplay = artistDisplay.Trim();
            master.ArtistKey = CatalogKeys.NameKey(artistDisplay);
        }

        if (year is int y) master.Year = y;
        if (!string.IsNullOrWhiteSpace(thumb)) master.Thumb = thumb.Trim();
    }

    private static void ApplyListingFields(
        CatalogRelease release, string? title, string? artistDisplay, int? year, string? thumb,
        string? format = null, string? label = null, string? catno = null)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            release.Title = title.Trim();
            release.TitleKey = CatalogKeys.TitleKey(title);
        }
        else if (string.IsNullOrEmpty(release.Title))
        {
            release.Title = $"Release {release.DiscogsId}";
            release.TitleKey = CatalogKeys.TitleKey(release.Title);
        }

        if (!string.IsNullOrWhiteSpace(artistDisplay))
        {
            release.ArtistDisplay = artistDisplay.Trim();
            release.ArtistKey = CatalogKeys.NameKey(artistDisplay);
        }

        if (year is int y) release.Year = y;
        if (!string.IsNullOrWhiteSpace(thumb)) release.Thumb = thumb.Trim();
        if (!string.IsNullOrWhiteSpace(format)) release.FormatSummary = format.Trim();
        if (!string.IsNullOrWhiteSpace(label)) release.LabelSummary = label.Trim();
        if (!string.IsNullOrWhiteSpace(catno)) release.CatalogNumberSummary = catno.Trim();
    }

    private static IEnumerable<(long Id, string Name)> CollectRelatedArtists(DiscogsArtist src)
    {
        IEnumerable<DiscogsArtistRef> Empty() => [];
        foreach (var r in (src.Aliases ?? Empty()).Concat(src.Members ?? Empty()).Concat(src.Groups ?? Empty()))
        {
            if (r.Id > 0)
                yield return (r.Id, r.Name ?? $"Artist {r.Id}");
        }
    }

    private static void CollectCreditArtists(IReadOnlyList<DiscogsArtistRef>? list, List<(long Id, string Name)> into)
    {
        if (list is null) return;
        foreach (var a in list)
        {
            if (a.Id > 0)
                into.Add((a.Id, a.Name ?? $"Artist {a.Id}"));
        }
    }

    private static void CollectLabelRefs(IReadOnlyList<DiscogsLabelRef>? list, List<(long Id, string Name)> into)
    {
        if (list is null) return;
        foreach (var l in list)
        {
            if (l.Id > 0)
                into.Add((l.Id, l.Name ?? $"Label {l.Id}"));
        }
    }

    private static IEnumerable<DiscogsTrack> FlattenTracks(IReadOnlyList<DiscogsTrack> tracks)
    {
        foreach (var track in tracks)
        {
            yield return track;
            if (track.SubTracks is null) continue;
            foreach (var sub in FlattenTracks(track.SubTracks))
                yield return sub;
        }
    }

    private static string? JoinArtists(IReadOnlyList<DiscogsArtistRef>? artists)
    {
        if (artists is null || artists.Count == 0) return null;
        var sb = new StringBuilder();
        for (var i = 0; i < artists.Count; i++)
        {
            var name = CatalogKeys.StripDisambiguator(artists[i].Name);
            if (name.Length == 0) continue;
            if (sb.Length > 0)
            {
                var join = artists[i - 1].Join;
                sb.Append(string.IsNullOrWhiteSpace(join) ? " / " : $" {join.Trim()} ");
            }

            sb.Append(name);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string? BuildFormatSummary(IReadOnlyList<DiscogsFormat>? formats)
    {
        if (formats is null || formats.Count == 0) return null;
        var parts = new List<string>();
        foreach (var format in formats)
        {
            var bits = new List<string>();
            if (!string.IsNullOrWhiteSpace(format.Name)) bits.Add(format.Name.Trim());
            if (format.Descriptions is not null)
                bits.AddRange(format.Descriptions.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()));
            if (bits.Count > 0) parts.Add(string.Join(", ", bits));
        }

        return parts.Count == 0 ? null : Truncate(string.Join(" / ", parts), 512);
    }

    private static string? BuildLabelSummary(IReadOnlyList<DiscogsLabelRef>? labels)
    {
        if (labels is null || labels.Count == 0) return null;
        var names = labels
            .Select(l => CatalogKeys.StripDisambiguator(l.Name))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names.Count == 0 ? null : Truncate(string.Join(", ", names), 512);
    }

    private static string? BuildCatalogNumberSummary(IReadOnlyList<DiscogsLabelRef>? labels)
    {
        if (labels is null || labels.Count == 0) return null;
        var cats = labels
            .Select(l => l.Catno?.Trim())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return cats.Count == 0 ? null : Truncate(string.Join(", ", cats!), 256);
    }

    private static string? FirstImageUri(IReadOnlyList<DiscogsImage>? images) =>
        images?.Select(i => NullIfBlank(i.Uri) ?? NullIfBlank(i.Uri150)).FirstOrDefault(u => u is not null);

    internal static int? ParseDurationSeconds(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration)) return null;
        var parts = duration.Trim().Split(':');
        if (parts.Length is < 2 or > 3) return null;
        if (!parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            return null;

        var nums = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return nums.Length == 2
            ? nums[0] * 60 + nums[1]
            : nums[0] * 3600 + nums[1] * 60 + nums[2];
    }

    internal static (int? Disc, int? Track) ParsePosition(string? position)
    {
        if (string.IsNullOrWhiteSpace(position)) return (null, null);
        var p = position.Trim();

        var discTrack = DiscTrackPosition().Match(p);
        if (discTrack.Success)
            return (int.Parse(discTrack.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(discTrack.Groups[2].Value, CultureInfo.InvariantCulture));

        var sideTrack = SideTrackPosition().Match(p);
        if (sideTrack.Success)
            return (sideTrack.Groups[1].Value[0] - 'A' + 1,
                int.Parse(sideTrack.Groups[2].Value, CultureInfo.InvariantCulture));

        var sideOnly = SideOnlyPosition().Match(p);
        if (sideOnly.Success)
            return (sideOnly.Groups[1].Value[0] - 'A' + 1, null);

        if (int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var trackOnly))
            return (null, trackOnly);

        return (null, null);
    }

    private static int? ParseYear(string? released)
    {
        if (string.IsNullOrWhiteSpace(released)) return null;
        var digits = new string(released.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y > 0 ? y : null;
    }

    private static string Sha256Hex(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? SerializeJson(IReadOnlyList<string>? values) =>
        values is null || values.Count == 0 ? null : JsonSerializer.Serialize(values);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value[..max];
    }

    [GeneratedRegex(@"^(\d+)\s*[-./]\s*(\d+)$")]
    private static partial Regex DiscTrackPosition();

    [GeneratedRegex(@"^([A-Za-z])(\d+)$")]
    private static partial Regex SideTrackPosition();

    [GeneratedRegex(@"^([A-Za-z])$")]
    private static partial Regex SideOnlyPosition();
}
