using MusicHoarder.Api.Discogs;

namespace MusicHoarder.Api.Tests.Catalog.Ingestion;

internal sealed class FakeDiscogsClient : IDiscogsClient
{
    public bool IsEnabled { get; set; } = true;
    public bool IsAuthenticated { get; set; } = true;
    public DiscogsRateLimitSnapshot? LastRateLimit { get; set; }

    public Dictionary<long, DiscogsDocument<DiscogsArtist>> Artists { get; } = new();
    public Dictionary<long, DiscogsDocument<DiscogsLabel>> Labels { get; } = new();
    public Dictionary<long, DiscogsDocument<DiscogsMaster>> Masters { get; } = new();
    public Dictionary<long, DiscogsDocument<DiscogsRelease>> Releases { get; } = new();
    public Dictionary<long, List<DiscogsPage<DiscogsArtistReleaseEntry>>> ArtistReleases { get; } = new();
    public Dictionary<long, List<DiscogsPage<DiscogsLabelReleaseEntry>>> LabelReleases { get; } = new();
    public Dictionary<long, List<DiscogsPage<DiscogsMasterVersion>>> MasterVersions { get; } = new();
    public Dictionary<string, DiscogsImageBytes> Images { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Exception> ImageErrors { get; } = new(StringComparer.Ordinal);
    public HashSet<long> NotFoundArtists { get; } = [];
    public HashSet<long> NotFoundLabels { get; } = [];
    public HashSet<long> NotFoundMasters { get; } = [];
    public HashSet<long> NotFoundReleases { get; } = [];
    public bool Throw404AsException { get; set; }

    public int ArtistCalls { get; set; }
    public int LabelCalls { get; set; }
    public int MasterCalls { get; set; }
    public int ReleaseCalls { get; set; }
    public int ArtistReleaseCalls { get; set; }
    public int ImageDownloadCalls { get; set; }

    public Task<DiscogsDocument<DiscogsArtist>?> GetArtistAsync(long id, CancellationToken ct = default)
    {
        ArtistCalls++;
        if (NotFoundArtists.Contains(id))
            return ThrowOrNull<DiscogsArtist>(id);
        return Task.FromResult(Artists.GetValueOrDefault(id));
    }

    public Task<DiscogsDocument<DiscogsLabel>?> GetLabelAsync(long id, CancellationToken ct = default)
    {
        LabelCalls++;
        if (NotFoundLabels.Contains(id))
            return ThrowOrNull<DiscogsLabel>(id);
        return Task.FromResult(Labels.GetValueOrDefault(id));
    }

    public Task<DiscogsDocument<DiscogsMaster>?> GetMasterAsync(long id, CancellationToken ct = default)
    {
        MasterCalls++;
        if (NotFoundMasters.Contains(id))
            return ThrowOrNull<DiscogsMaster>(id);
        return Task.FromResult(Masters.GetValueOrDefault(id));
    }

    public Task<DiscogsDocument<DiscogsRelease>?> GetReleaseAsync(long id, CancellationToken ct = default)
    {
        ReleaseCalls++;
        if (NotFoundReleases.Contains(id))
            return ThrowOrNull<DiscogsRelease>(id);
        return Task.FromResult(Releases.GetValueOrDefault(id));
    }

    public Task<DiscogsPage<DiscogsArtistReleaseEntry>?> GetArtistReleasesAsync(
        long id, int page = 1, int perPage = 100, CancellationToken ct = default)
    {
        ArtistReleaseCalls++;
        if (!ArtistReleases.TryGetValue(id, out var pages) || page < 1 || page > pages.Count)
            return Task.FromResult<DiscogsPage<DiscogsArtistReleaseEntry>?>(null);
        return Task.FromResult<DiscogsPage<DiscogsArtistReleaseEntry>?>(pages[page - 1]);
    }

    public Task<DiscogsPage<DiscogsLabelReleaseEntry>?> GetLabelReleasesAsync(
        long id, int page = 1, int perPage = 100, CancellationToken ct = default)
    {
        if (!LabelReleases.TryGetValue(id, out var pages) || page < 1 || page > pages.Count)
            return Task.FromResult<DiscogsPage<DiscogsLabelReleaseEntry>?>(null);
        return Task.FromResult<DiscogsPage<DiscogsLabelReleaseEntry>?>(pages[page - 1]);
    }

    public Task<DiscogsPage<DiscogsMasterVersion>?> GetMasterVersionsAsync(
        long id, int page = 1, int perPage = 100, DiscogsVersionFilter? filter = null, CancellationToken ct = default)
    {
        if (!MasterVersions.TryGetValue(id, out var pages) || page < 1 || page > pages.Count)
            return Task.FromResult<DiscogsPage<DiscogsMasterVersion>?>(null);
        return Task.FromResult<DiscogsPage<DiscogsMasterVersion>?>(pages[page - 1]);
    }

    public Task<DiscogsPage<DiscogsSearchResult>> SearchAsync(
        DiscogsSearchQuery query, int page = 1, int perPage = 25, CancellationToken ct = default) =>
        Task.FromResult(new DiscogsPage<DiscogsSearchResult>(new DiscogsPagination(page, 0, perPage, 0), []));

    public Task<DiscogsImageBytes?> DownloadImageAsync(string signedUrl, CancellationToken ct = default)
    {
        ImageDownloadCalls++;
        if (ImageErrors.TryGetValue(signedUrl, out var error))
            return Task.FromException<DiscogsImageBytes?>(error);
        return Task.FromResult(Images.GetValueOrDefault(signedUrl));
    }

    private Task<DiscogsDocument<T>?> ThrowOrNull<T>(long id)
    {
        if (Throw404AsException)
            throw new DiscogsApiException($"Not found: {id}", 404);
        return Task.FromResult<DiscogsDocument<T>?>(null);
    }
}
