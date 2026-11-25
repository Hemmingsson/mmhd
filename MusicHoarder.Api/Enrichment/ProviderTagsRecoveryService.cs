using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Restores original terms from retained provider evidence without calling providers or rewriting files.
/// Bounded batches and compare-before-update predicates keep a concurrent enrichment's changes intact.
/// Repeated startup passes are idempotent, including after interruption or a taxonomy update.
/// </summary>
internal sealed class ProviderTagsRecoveryService(
    IServiceScopeFactory scopeFactory, ILogger<ProviderTagsRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            var cursor = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
                var songs = await db.Songs.IgnoreQueryFilters().AsNoTracking()
                    .Where(s => s.Id > cursor && s.DeletedAtUtc == null).OrderBy(s => s.Id).Take(200)
                    .Select(s => new { s.Id, s.Genre, s.Tags, s.Artist, s.Title, s.Isrc, s.MusicBrainzId, s.SpotifyId })
                    .ToListAsync(stoppingToken);
                if (songs.Count == 0) break;
                var ids = songs.Select(s => s.Id).ToArray();
                var attempts = await db.SongProviderAttempts.IgnoreQueryFilters().AsNoTracking()
                    .Where(a => ids.Contains(a.SongId) && a.Status == ProviderAttemptStatus.Matched && a.MatchedDataJson != null)
                    .ToListAsync(stoppingToken);
                foreach (var song in songs)
                {
                    var recovered = ProviderTags.Recover(song.Genre, song.Tags, song.Artist, song.Title,
                        song.Isrc, song.MusicBrainzId, song.SpotifyId, attempts.Where(a => a.SongId == song.Id));
                    if (song.Genre == recovered.Genre && song.Tags.SequenceEqual(recovered.Tags)) continue;
                    await db.Songs.IgnoreQueryFilters()
                        .Where(s => s.Id == song.Id && s.DeletedAtUtc == null && s.Genre == song.Genre && s.Tags.SequenceEqual(song.Tags)
                            && s.Artist == song.Artist && s.Title == song.Title && s.Isrc == song.Isrc
                            && s.MusicBrainzId == song.MusicBrainzId && s.SpotifyId == song.SpotifyId)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.Tags, recovered.Tags)
                            .SetProperty(s => s.Genre, recovered.Genre), stoppingToken);
                }
                cursor = songs[^1].Id;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Recovery is optional; a failed pass must never take the music library offline.
            logger.LogWarning(ex, "Provider tag recovery interrupted; remaining evidence will be retried on next startup");
        }
    }
}
