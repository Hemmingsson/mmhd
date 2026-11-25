using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Catalog.Backfill;

/// <summary>
/// Periodically advances every <see cref="CatalogBackfillStatus.Running"/> run when
/// <see cref="DiscogsOptions.EnableBackfill"/> is on.
/// </summary>
public sealed class CatalogBackfillWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<DiscogsOptions> options,
    ILogger<CatalogBackfillWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small startup delay so the host finishes wiring DI / migrations.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = options.CurrentValue;
            if (!opts.EnableBackfill)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, opts.BackfillIntervalMinutes)), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            try
            {
                await TickAsync(opts, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Catalog backfill worker tick failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, opts.BackfillIntervalMinutes)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(DiscogsOptions opts, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var backfill = scope.ServiceProvider.GetRequiredService<ICatalogBackfillService>();

        var running = await db.CatalogBackfillRuns
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.Status == CatalogBackfillStatus.Running)
            .Select(r => new { r.Id, r.OwnerUserId })
            .ToListAsync(ct);

        foreach (var run in running)
        {
            try
            {
                // RunBatchAsync scopes all work to the run's OwnerUserId (explicit filters).
                await backfill.RunBatchAsync(run.Id, opts.BackfillBatchSize, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Catalog backfill run {RunId} (owner {OwnerId}) failed this tick",
                    run.Id, run.OwnerUserId);
            }
        }
    }
}
