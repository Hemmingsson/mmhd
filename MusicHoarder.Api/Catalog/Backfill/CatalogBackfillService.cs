using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Catalog.Identity;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Catalog.Backfill;

public sealed class CatalogBackfillService(
    MusicHoarderDbContext db,
    ICatalogIdentityService identity,
    ICatalogFetcher fetcher,
    ICatalogImageDownloader images,
    IDiscogsClient discogs) : ICatalogBackfillService
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
        var exhausted = true;
        // The client is shared, so requests other callers send during the batch count too.
        var requestsBefore = discogs.RequestsSent;

        try
        {
            exhausted = run.Kind switch
            {
                CatalogBackfillKind.LibraryArtists => await ProcessLibraryArtistsAsync(run, budget, ct),
                CatalogBackfillKind.LibraryAlbums => await ProcessLibraryAlbumsAsync(run, budget, ct),
                CatalogBackfillKind.CatalogResources => await ProcessCatalogResourcesAsync(run, budget, ct),
                CatalogBackfillKind.Images => await ProcessImagesAsync(run, budget, ct),
                _ => true,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Rate limits and failures outside a single subject (an unwritable image directory, a
            // dropped connection) pause the run; the next batch resumes from the saved cursor.
            db.ChangeTracker.Clear();
            run = await db.CatalogBackfillRuns.IgnoreQueryFilters().FirstAsync(r => r.Id == runId, ct);
            AddRequests(run, requestsBefore);
            run.Status = CatalogBackfillStatus.Paused;
            run.LastError = ex.Message;
            run.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return ToDto(run);
        }

        AddRequests(run, requestsBefore);

        if (run.Status == CatalogBackfillStatus.Running && exhausted)
        {
            run.Status = CatalogBackfillStatus.Completed;
            run.FinishedAtUtc = DateTime.UtcNow;
        }

        await SaveProgressAsync(run, ct);
        return ToDto(run);
    }

    /// <summary>
    /// Saves the run's in-memory progress. A subject's ingest can clear the shared change tracker,
    /// which detaches the run; the in-memory values are this batch's truth, so they are re-attached.
    /// </summary>
    private async Task SaveProgressAsync(CatalogBackfillRun run, CancellationToken ct)
    {
        run.UpdatedAtUtc = DateTime.UtcNow;
        if (db.Entry(run).State == EntityState.Detached)
        {
            var tracked = db.ChangeTracker.Entries<CatalogBackfillRun>().FirstOrDefault(e => e.Entity.Id == run.Id);
            if (tracked is null)
                db.CatalogBackfillRuns.Update(run);
            else
                tracked.CurrentValues.SetValues(run);
        }

        await db.SaveChangesAsync(ct);
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

    /// <returns>Whether the run has nothing left after this batch.</returns>
    private async Task<bool> ProcessLibraryArtistsAsync(CatalogBackfillRun run, int budget, CancellationToken ct)
    {
        var artists = await ListLibraryArtistsAsync(run.OwnerUserId, run.Cursor, budget, ct);
        foreach (var artist in artists)
        {
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
                FailSubject(run, ex);
            }

            run.Cursor = DiscogsMatchScoring.ArtistSubjectKey(artist);
            await SaveProgressAsync(run, ct);
        }

        return artists.Count < budget;
    }

    private async Task<bool> ProcessLibraryAlbumsAsync(CatalogBackfillRun run, int budget, CancellationToken ct)
    {
        var albums = await ListLibraryAlbumsAsync(run.OwnerUserId, run.Cursor, budget, ct);
        foreach (var (albumArtist, album, unlinkedSongIds) in albums)
        {
            try
            {
                var view = await identity.ResolveAlbumAsync(run.OwnerUserId, albumArtist, album, run.Id, run.DryRun, ct);
                run.Processed++;
                if (view.Accepted is not null && !run.DryRun)
                {
                    run.Linked++;
                    run.SongsUpdated += await db.Songs
                        .IgnoreQueryFilters()
                        .CountAsync(s => unlinkedSongIds.Contains(s.Id) && s.DiscogsReleaseId != null, ct);
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
                FailSubject(run, ex);
            }

            run.Cursor = DiscogsMatchScoring.AlbumSubjectKey(albumArtist, album);
            await SaveProgressAsync(run, ct);
        }

        return albums.Count < budget;
    }

    private void FailSubject(CatalogBackfillRun run, Exception ex)
    {
        // Drop whatever the failed subject left tracked so the progress save does not retry it.
        db.ChangeTracker.Clear();
        run.Failed++;
        run.Processed++;
        run.LastError = ex.Message;
    }

    private const string ReleasePhase = "r:";
    private const string ArtistPhase = "a:";

    /// <summary>
    /// Refreshes stale releases, then stale artists, in Discogs id order. The cursor names the phase
    /// (<c>r:123</c>, <c>a:45</c>); a bare number is a release cursor from before phases existed.
    /// </summary>
    private async Task<bool> ProcessCatalogResourcesAsync(CatalogBackfillRun run, int budget, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var (inArtistPhase, cursorId) = ParseResourceCursor(run.Cursor);
        var remaining = budget;

        if (!inArtistPhase)
        {
            var releases = await db.CatalogReleases
                .AsNoTracking()
                .Where(r => r.FetchState == CatalogFetchState.Fetched
                    && r.RefreshAfterUtc != null
                    && r.RefreshAfterUtc <= now
                    && r.DiscogsId > cursorId)
                .OrderBy(r => r.DiscogsId)
                .Select(r => r.DiscogsId)
                .Take(remaining)
                .ToListAsync(ct);
            foreach (var id in releases)
            {
                await fetcher.EnsureReleaseAsync(id, force: true, ct);
                run.Processed++;
                run.Linked++;
                run.Cursor = ReleasePhase + id;
                await SaveProgressAsync(run, ct);
            }

            remaining -= releases.Count;
            cursorId = 0;
        }

        if (remaining == 0)
            return false;

        var artists = await db.CatalogArtists
            .AsNoTracking()
            .Where(a => a.RefreshAfterUtc != null && a.RefreshAfterUtc <= now && a.DiscogsId > cursorId)
            .OrderBy(a => a.DiscogsId)
            .Select(a => a.DiscogsId)
            .Take(remaining)
            .ToListAsync(ct);
        foreach (var id in artists)
        {
            await fetcher.EnsureArtistAsync(id, withDiscography: true, force: true, ct);
            run.Processed++;
            run.Linked++;
            run.Cursor = ArtistPhase + id;
            await SaveProgressAsync(run, ct);
        }

        return artists.Count < remaining;
    }

    private static (bool ArtistPhase, long Id) ParseResourceCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
            return (false, 0);
        if (cursor.StartsWith(ArtistPhase, StringComparison.Ordinal))
            return (true, long.TryParse(cursor.AsSpan(ArtistPhase.Length), out var a) ? a : 0);
        if (cursor.StartsWith(ReleasePhase, StringComparison.Ordinal))
            return (false, long.TryParse(cursor.AsSpan(ReleasePhase.Length), out var r) ? r : 0);
        return (false, long.TryParse(cursor, out var legacy) ? legacy : 0);
    }

    private async Task<bool> ProcessImagesAsync(CatalogBackfillRun run, int budget, CancellationToken ct)
    {
        var downloadedBefore = await CountDownloadedImagesAsync(ct);
        var attempted = await images.DownloadPendingAsync(budget, ct);
        run.Processed += attempted;
        run.Linked += Math.Clamp(await CountDownloadedImagesAsync(ct) - downloadedBefore, 0, attempted);
        run.Cursor = DateTime.UtcNow.Ticks.ToString();
        await SaveProgressAsync(run, ct);
        // Failed downloads count as attempted, so only an empty pass means nothing is pending.
        return attempted == 0;
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

    /// <summary>
    /// The next albums with songs lacking a release id, each with those songs' ids, so the songs a
    /// link newly fills can be counted by primary key.
    /// </summary>
    private async Task<List<(string AlbumArtist, string Album, List<int> UnlinkedSongIds)>> ListLibraryAlbumsAsync(
        Guid ownerId, string? cursor, int take, CancellationToken ct)
    {
        var songs = await db.Songs
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(s => s.OwnerUserId == ownerId && s.DeletedAtUtc == null && s.Album != null && s.DiscogsReleaseId == null)
            .Select(s => new { s.Id, s.AlbumArtist, s.Artist, s.Album })
            .ToListAsync(ct);

        return songs
            .Select(s => (
                s.Id,
                AlbumArtist: (s.AlbumArtist ?? s.Artist)?.Trim() ?? "",
                Album: s.Album!.Trim()))
            .Where(s => s.AlbumArtist.Length > 0 && s.Album.Length > 0)
            .GroupBy(s => DiscogsMatchScoring.AlbumSubjectKey(s.AlbumArtist, s.Album), StringComparer.Ordinal)
            .Where(g => string.IsNullOrEmpty(cursor) || string.CompareOrdinal(g.Key, cursor) > 0)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Take(take)
            .Select(g => (g.First().AlbumArtist, g.First().Album, g.Select(s => s.Id).ToList()))
            .ToList();
    }

    private Task<int> CountDownloadedImagesAsync(CancellationToken ct) =>
        db.CatalogImages.CountAsync(i => i.Status == CatalogImageStatus.Downloaded, ct);

    private void AddRequests(CatalogBackfillRun run, long before) =>
        run.RequestsUsed += (int)Math.Clamp(discogs.RequestsSent - before, 0, int.MaxValue);

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
