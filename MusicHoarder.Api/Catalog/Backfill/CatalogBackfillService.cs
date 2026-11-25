using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Catalog.Identity;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Catalog.Backfill;

public sealed class CatalogBackfillService(
    MusicHoarderDbContext db,
    ICatalogIdentityService identity,
    ICatalogFetcher fetcher,
    ICatalogImageDownloader images) : ICatalogBackfillService
{
    public async Task<CatalogBackfillRunDto> StartAsync(
        Guid ownerId, CatalogBackfillKind kind, bool dryRun, string startedBy, CancellationToken ct = default)
    {
        var paused = await db.CatalogBackfillRuns
            .IgnoreQueryFilters()
            .Where(r => r.OwnerUserId == ownerId && r.Kind == kind && r.Status == CatalogBackfillStatus.Paused)
            .OrderByDescending(r => r.UpdatedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (paused is not null)
        {
            paused.Status = CatalogBackfillStatus.Running;
            paused.LastError = null;
            paused.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return ToDto(paused);
        }

        var now = DateTime.UtcNow;
        var run = new CatalogBackfillRun
        {
            OwnerUserId = ownerId,
            Kind = kind,
            Status = CatalogBackfillStatus.Running,
            DryRun = dryRun,
            StartedBy = startedBy,
            StartedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.CatalogBackfillRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return ToDto(run);
    }

    public async Task<CatalogBackfillRunDto> RunBatchAsync(int runId, int batchSize, CancellationToken ct = default)
    {
        var run = await db.CatalogBackfillRuns
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new InvalidOperationException($"Backfill run {runId} not found.");

        if (run.Status is CatalogBackfillStatus.Completed or CatalogBackfillStatus.RolledBack or CatalogBackfillStatus.Cancelled)
            return ToDto(run);

        run.Status = CatalogBackfillStatus.Running;
        run.LastError = null;
        var budget = Math.Clamp(batchSize, 1, 100_000);
        var processedThisBatch = 0;

        try
        {
            switch (run.Kind)
            {
                case CatalogBackfillKind.LibraryArtists:
                    processedThisBatch = await ProcessLibraryArtistsAsync(run, budget, ct);
                    break;
                case CatalogBackfillKind.LibraryAlbums:
                    processedThisBatch = await ProcessLibraryAlbumsAsync(run, budget, ct);
                    break;
                case CatalogBackfillKind.CatalogResources:
                    processedThisBatch = await ProcessCatalogResourcesAsync(run, budget, ct);
                    break;
                case CatalogBackfillKind.Images:
                    processedThisBatch = await ProcessImagesAsync(run, budget, ct);
                    break;
            }
        }
        catch (ProviderRateLimitedException ex)
        {
            run.Status = CatalogBackfillStatus.Paused;
            run.LastError = ex.Message;
            run.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return ToDto(run);
        }

        // Finished when this pass found nothing, or fewer subjects than the budget (end of the list).
        if (run.Status == CatalogBackfillStatus.Running
            && (processedThisBatch == 0 || processedThisBatch < budget))
        {
            run.Status = CatalogBackfillStatus.Completed;
            run.FinishedAtUtc = DateTime.UtcNow;
        }

        run.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToDto(run);
    }

    public async Task<CatalogBackfillRunDto?> RollbackAsync(Guid ownerId, int runId, CancellationToken ct = default)
    {
        var run = await db.CatalogBackfillRuns
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == runId && r.OwnerUserId == ownerId, ct);
        if (run is null)
            return null;

        if (run.Status == CatalogBackfillStatus.RolledBack)
            return ToDto(run);

        var source = run.ChangeSource;
        var changes = await db.SongMetadataChanges
            .IgnoreQueryFilters()
            .Include(c => c.Song)
            .Where(c => c.Source == source && c.RevertedAtUtc == null && c.AppliedAtUtc != null
                && c.Song.OwnerUserId == ownerId)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var change in changes)
        {
            var current = ReadField(change.Song, change.FieldName);
            if (!string.Equals(current, change.NewValue, StringComparison.Ordinal))
            {
                // User (or something else) edited the value — leave it alone, still mark reverted.
                change.RevertedAtUtc = now;
                continue;
            }

            WriteField(change.Song, change.FieldName, change.OldValue);
            change.RevertedAtUtc = now;
        }

        var decisions = await db.CatalogIdentityDecisions
            .IgnoreQueryFilters()
            .Where(d => d.RunId == runId && d.OwnerUserId == ownerId
                && d.Status != CatalogIdentityStatus.Reverted
                && d.Status != CatalogIdentityStatus.Superseded)
            .ToListAsync(ct);
        foreach (var d in decisions)
            d.Status = CatalogIdentityStatus.Reverted;

        run.Status = CatalogBackfillStatus.RolledBack;
        run.RolledBackAtUtc = now;
        run.UpdatedAtUtc = now;
        run.FinishedAtUtc ??= now;
        await db.SaveChangesAsync(ct);
        return ToDto(run);
    }

    public async Task<IReadOnlyList<CatalogBackfillRunDto>> ListRunsAsync(Guid ownerId, int limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 200);
        var rows = await db.CatalogBackfillRuns
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.OwnerUserId == ownerId)
            .OrderByDescending(r => r.StartedAtUtc)
            .Take(take)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    private async Task<int> ProcessLibraryArtistsAsync(CatalogBackfillRun run, int budget, CancellationToken ct)
    {
        var artists = await ListLibraryArtistsAsync(run.OwnerUserId, run.Cursor, budget, ct);
        if (artists.Count == 0)
            return 0;

        foreach (var artist in artists)
        {
            var before = SnapshotCounters(run);
            try
            {
                var view = await identity.ResolveArtistAsync(run.OwnerUserId, artist, run.Id, run.DryRun, ct);
                run.Processed++;
                if (view.Accepted is not null && !run.DryRun)
                    run.Linked++;
                else if (view.Candidates.Count > 0 || (view.Accepted is not null && run.DryRun))
                    run.Candidates++;
                else
                    run.Unmatched++;
            }
            catch (ProviderRateLimitedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                run.Failed++;
                run.Processed++;
                run.LastError = ex.Message;
            }

            run.Cursor = DiscogsMatchScoring.ArtistSubjectKey(artist);
            run.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            _ = before;
        }

        return artists.Count;
    }

    private async Task<int> ProcessLibraryAlbumsAsync(CatalogBackfillRun run, int budget, CancellationToken ct)
    {
        var albums = await ListLibraryAlbumsAsync(run.OwnerUserId, run.Cursor, budget, ct);
        if (albums.Count == 0)
            return 0;

        foreach (var (albumArtist, album) in albums)
        {
            var songsBefore = await CountSongsWithReleaseAsync(run.OwnerUserId, albumArtist, album, ct);
            try
            {
                var view = await identity.ResolveAlbumAsync(run.OwnerUserId, albumArtist, album, run.Id, run.DryRun, ct);
                run.Processed++;
                if (view.Accepted is not null && !run.DryRun)
                {
                    run.Linked++;
                    var songsAfter = await CountSongsWithReleaseAsync(run.OwnerUserId, albumArtist, album, ct);
                    run.SongsUpdated += Math.Max(0, songsAfter - songsBefore);
                }
                else if (view.Candidates.Count > 0 || (view.Accepted is not null && run.DryRun))
                    run.Candidates++;
                else
                    run.Unmatched++;
            }
            catch (ProviderRateLimitedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                run.Failed++;
                run.Processed++;
                run.LastError = ex.Message;
            }

            run.Cursor = DiscogsMatchScoring.AlbumSubjectKey(albumArtist, album);
            run.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return albums.Count;
    }

    private async Task<int> ProcessCatalogResourcesAsync(CatalogBackfillRun run, int budget, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var releases = await db.CatalogReleases
            .Where(r => r.FetchState == CatalogFetchState.Fetched
                && r.RefreshAfterUtc != null
                && r.RefreshAfterUtc <= now)
            .OrderBy(r => r.DiscogsId)
            .Select(r => r.DiscogsId)
            .ToListAsync(ct);

        var cursorId = long.TryParse(run.Cursor, out var c) ? c : 0;
        var batch = releases.Where(id => id > cursorId).Take(budget).ToList();
        if (batch.Count == 0)
        {
            // Also refresh stale masters/artists lightly when no releases.
            var artists = await db.CatalogArtists
                .Where(a => a.RefreshAfterUtc != null && a.RefreshAfterUtc <= now)
                .OrderBy(a => a.DiscogsId)
                .Select(a => a.DiscogsId)
                .Where(id => id > cursorId)
                .Take(budget)
                .ToListAsync(ct);
            foreach (var id in artists)
            {
                await fetcher.EnsureArtistAsync(id, withDiscography: true, force: true, ct);
                run.Processed++;
                run.Linked++;
                run.Cursor = id.ToString();
                run.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            return artists.Count;
        }

        foreach (var id in batch)
        {
            await fetcher.EnsureReleaseAsync(id, force: true, ct);
            run.Processed++;
            run.Linked++;
            run.Cursor = id.ToString();
            run.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return batch.Count;
    }

    private async Task<int> ProcessImagesAsync(CatalogBackfillRun run, int budget, CancellationToken ct)
    {
        var downloaded = await images.DownloadPendingAsync(budget, ct);
        run.Processed += downloaded;
        run.Linked += downloaded;
        run.Cursor = DateTime.UtcNow.Ticks.ToString();
        run.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        // Nothing left when zero downloaded.
        return downloaded;
    }

    private async Task<List<string>> ListLibraryArtistsAsync(Guid ownerId, string? cursor, int take, CancellationToken ct)
    {
        var songs = await db.Songs
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(s => s.OwnerUserId == ownerId && s.DeletedAtUtc == null)
            .Select(s => new { s.AlbumArtist, s.Artist })
            .ToListAsync(ct);

        var artists = songs
            .Select(s => (s.AlbumArtist ?? s.Artist)?.Trim())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .DistinctBy(a => DiscogsMatchScoring.ArtistSubjectKey(a!), StringComparer.Ordinal)
            .Select(a => a!)
            .OrderBy(a => DiscogsMatchScoring.ArtistSubjectKey(a), StringComparer.Ordinal)
            .ToList();

        if (!string.IsNullOrEmpty(cursor))
            artists = artists.Where(a => string.CompareOrdinal(DiscogsMatchScoring.ArtistSubjectKey(a), cursor) > 0).ToList();

        return artists.Take(take).ToList();
    }

    private async Task<List<(string AlbumArtist, string Album)>> ListLibraryAlbumsAsync(
        Guid ownerId, string? cursor, int take, CancellationToken ct)
    {
        var songs = await db.Songs
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(s => s.OwnerUserId == ownerId && s.DeletedAtUtc == null && s.Album != null && s.DiscogsReleaseId == null)
            .Select(s => new { s.AlbumArtist, s.Artist, s.Album })
            .ToListAsync(ct);

        var albums = songs
            .Select(s => (
                AlbumArtist: (s.AlbumArtist ?? s.Artist)?.Trim() ?? "",
                Album: s.Album!.Trim()))
            .Where(a => a.AlbumArtist.Length > 0 && a.Album.Length > 0)
            .DistinctBy(a => DiscogsMatchScoring.AlbumSubjectKey(a.AlbumArtist, a.Album), StringComparer.Ordinal)
            .OrderBy(a => DiscogsMatchScoring.AlbumSubjectKey(a.AlbumArtist, a.Album), StringComparer.Ordinal)
            .ToList();

        if (!string.IsNullOrEmpty(cursor))
        {
            albums = albums
                .Where(a => string.CompareOrdinal(DiscogsMatchScoring.AlbumSubjectKey(a.AlbumArtist, a.Album), cursor) > 0)
                .ToList();
        }

        return albums.Take(take).ToList();
    }

    private async Task<int> CountSongsWithReleaseAsync(Guid ownerId, string albumArtist, string album, CancellationToken ct)
    {
        var artistKey = CatalogKeys.NameKey(albumArtist);
        var albumKey = CatalogKeys.TitleKey(album);
        var songs = await db.Songs
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(s => s.OwnerUserId == ownerId && s.DeletedAtUtc == null && s.DiscogsReleaseId != null)
            .Select(s => new { s.AlbumArtist, s.Artist, s.Album })
            .ToListAsync(ct);
        return songs.Count(s =>
            CatalogKeys.NameKey(s.AlbumArtist ?? s.Artist) == artistKey
            && CatalogKeys.TitleKey(s.Album) == albumKey);
    }

    private static (int Processed, int Linked, int Candidates) SnapshotCounters(CatalogBackfillRun run) =>
        (run.Processed, run.Linked, run.Candidates);

    private static string? ReadField(SongMetadata song, string fieldName) => fieldName switch
    {
        nameof(SongMetadata.DiscogsReleaseId) => song.DiscogsReleaseId?.ToString(),
        nameof(SongMetadata.DiscogsMasterId) => song.DiscogsMasterId?.ToString(),
        nameof(SongMetadata.DiscogsArtistId) => song.DiscogsArtistId?.ToString(),
        _ => null,
    };

    private static void WriteField(SongMetadata song, string fieldName, string? value)
    {
        long? parsed = string.IsNullOrWhiteSpace(value) ? null : long.Parse(value);
        switch (fieldName)
        {
            case nameof(SongMetadata.DiscogsReleaseId):
                song.DiscogsReleaseId = parsed;
                break;
            case nameof(SongMetadata.DiscogsMasterId):
                song.DiscogsMasterId = parsed;
                break;
            case nameof(SongMetadata.DiscogsArtistId):
                song.DiscogsArtistId = parsed;
                break;
        }
    }

    private static CatalogBackfillRunDto ToDto(CatalogBackfillRun run) =>
        new(
            run.Id,
            run.Kind.ToString(),
            run.Status.ToString(),
            run.DryRun,
            run.Cursor,
            run.Processed,
            run.Linked,
            run.Candidates,
            run.Unmatched,
            run.Failed,
            run.SongsUpdated,
            run.RequestsUsed,
            run.LastError,
            run.StartedBy,
            run.StartedAtUtc,
            run.UpdatedAtUtc,
            run.FinishedAtUtc);
}
