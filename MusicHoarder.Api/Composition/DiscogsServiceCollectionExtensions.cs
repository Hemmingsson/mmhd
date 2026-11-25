using Microsoft.Extensions.Options;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Catalog.Acquisition;
using MusicHoarder.Api.Catalog.Backfill;
using MusicHoarder.Api.Catalog.Identity;
using MusicHoarder.Api.Catalog.Ingestion;
using MusicHoarder.Api.Catalog.Query;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.AlbumTracklist;
using MusicHoarder.Api.Enrichment.AlbumTracklist.Providers;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Composition;

public static class DiscogsServiceCollectionExtensions
{
    /// <summary>
    /// The Discogs client, the catalog graph services, the supporting enrichment and tracklist
    /// providers and the backfill worker. Everything beyond reading known ids is off until its
    /// <see cref="DiscogsOptions"/> switch is on.
    /// </summary>
    public static IServiceCollection AddDiscogsCatalog(this IServiceCollection services)
    {
        services.AddOptions<DiscogsOptions>().BindConfiguration(DiscogsOptions.SectionName);

        // One client for the process: its request gate and last rate-limit headers are the shared
        // budget every caller draws on. It owns a plain HttpClient rather than a factory one: the
        // client paces, retries and honours 429 itself, and the factory's default resilience
        // handler would retry around that gate. The pooled-connection lifetime keeps DNS fresh.
        services.AddSingleton<DiscogsMetrics>();
        services.AddSingleton<DiscogsClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<DiscogsOptions>>();
            var http = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            });
            http.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.CurrentValue.TimeoutSeconds));
            return new DiscogsClient(
                http, options, sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
                sp.GetRequiredService<DiscogsMetrics>(), sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<DiscogsClient>>());
        });
        services.AddSingleton<IDiscogsClient>(sp => sp.GetRequiredService<DiscogsClient>());

        services.AddScoped<ICatalogIngestor, CatalogIngestor>();
        services.AddScoped<ICatalogFetcher, CatalogFetcher>();
        services.AddScoped<ICatalogImageDownloader, CatalogImageDownloader>();
        services.AddScoped<ICatalogIdentityService, CatalogIdentityService>();
        services.AddScoped<ICatalogBackfillService, CatalogBackfillService>();
        services.AddScoped<ICatalogQueryService, CatalogQueryService>();
        services.AddScoped<ICatalogEditionQueuer, CatalogEditionQueuer>();
        services.AddScoped<ICatalogAcquisitionService, CatalogAcquisitionService>();
        services.AddHostedService<CatalogBackfillWorker>();

        services.AddSingleton<DiscogsReleaseMatcher>();
        services.AddSingleton<IEnrichmentProvider, DiscogsEnrichmentProvider>();
        services.AddSingleton<IAlbumTracklistProvider, DiscogsAlbumTracklistProvider>();
        return services;
    }
}
