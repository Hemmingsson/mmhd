using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Catalog.Identity;

internal sealed class FakeCatalogFetcher : ICatalogFetcher
{
    public Dictionary<long, CatalogArtist> Artists { get; } = new();
    public Dictionary<long, CatalogRelease> Releases { get; } = new();
    public Dictionary<long, CatalogMaster> Masters { get; } = new();
    public List<long> EnsuredReleases { get; } = [];

    public Task<CatalogArtist?> EnsureArtistAsync(long discogsId, bool withDiscography, bool force = false, CancellationToken ct = default)
    {
        if (!Artists.TryGetValue(discogsId, out var artist))
        {
            artist = new CatalogArtist
            {
                Id = Artists.Count + 1,
                DiscogsId = discogsId,
                Name = $"Artist {discogsId}",
                NameKey = CatalogKeys.NameKey($"Artist {discogsId}"),
                FetchState = CatalogFetchState.Fetched,
                CreatedAtUtc = DateTime.UtcNow,
            };
            Artists[discogsId] = artist;
        }

        return Task.FromResult<CatalogArtist?>(artist);
    }

    public Task<CatalogLabel?> EnsureLabelAsync(long discogsId, bool withReleases, bool force = false, CancellationToken ct = default) =>
        Task.FromResult<CatalogLabel?>(null);

    public Task<CatalogMaster?> EnsureMasterAsync(long discogsId, bool withVersions, bool force = false, CancellationToken ct = default)
    {
        if (!Masters.TryGetValue(discogsId, out var master))
        {
            master = new CatalogMaster
            {
                Id = Masters.Count + 1,
                DiscogsId = discogsId,
                Title = $"Master {discogsId}",
                TitleKey = CatalogKeys.TitleKey($"Master {discogsId}"),
                FetchState = CatalogFetchState.Fetched,
                CreatedAtUtc = DateTime.UtcNow,
                MainReleaseDiscogsId = discogsId + 1000,
            };
            Masters[discogsId] = master;
        }

        return Task.FromResult<CatalogMaster?>(master);
    }

    public Task<CatalogRelease?> EnsureReleaseAsync(long discogsId, bool force = false, CancellationToken ct = default)
    {
        EnsuredReleases.Add(discogsId);
        if (!Releases.TryGetValue(discogsId, out var release))
        {
            release = new CatalogRelease
            {
                Id = Releases.Count + 1,
                DiscogsId = discogsId,
                Title = $"Release {discogsId}",
                TitleKey = CatalogKeys.TitleKey($"Release {discogsId}"),
                ArtistDisplay = "Aphex Twin",
                ArtistKey = CatalogKeys.NameKey("Aphex Twin"),
                MasterDiscogsId = discogsId + 500,
                FetchState = CatalogFetchState.Fetched,
                CreatedAtUtc = DateTime.UtcNow,
                Credits =
                [
                    new CatalogCredit
                    {
                        Sequence = 0,
                        Kind = CatalogCreditKind.Main,
                        ArtistDiscogsId = 45,
                        Name = "Aphex Twin",
                    },
                ],
            };
            Releases[discogsId] = release;
        }

        return Task.FromResult<CatalogRelease?>(release);
    }
}

internal sealed class FakeMusicBrainz : IMusicBrainzWebService
{
    public Dictionary<string, List<MusicBrainzUrlRelation>> ArtistRels { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<MusicBrainzUrlRelation>> ReleaseRels { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<MusicBrainzUrlRelation>> ReleaseGroupRels { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<MusicBrainzRecording?> LookupByRecordingIdAsync(string mbid, CancellationToken ct = default) =>
        Task.FromResult<MusicBrainzRecording?>(null);
    public Task<MusicBrainzRecording?> LookupByIsrcAsync(string isrc, CancellationToken ct = default) =>
        Task.FromResult<MusicBrainzRecording?>(null);
    public Task<IReadOnlyList<MusicBrainzRecording>> SearchAsync(string artist, string title, int limit, string? album = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MusicBrainzRecording>>([]);
    public Task<IReadOnlyList<MusicBrainzRecording>> SearchFreeTextAsync(string query, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MusicBrainzRecording>>([]);
    public Task<MusicBrainzRelease?> LookupReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<MusicBrainzRelease?>(null);
    public Task<IReadOnlyList<MusicBrainzReleaseSearchResult>> SearchReleasesAsync(string artist, string album, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MusicBrainzReleaseSearchResult>>([]);

    public Task<IReadOnlyList<MusicBrainzUrlRelation>> GetArtistUrlRelationsAsync(string mbid, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MusicBrainzUrlRelation>>(ArtistRels.GetValueOrDefault(mbid) ?? []);
    public Task<IReadOnlyList<MusicBrainzUrlRelation>> GetReleaseGroupUrlRelationsAsync(string mbid, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MusicBrainzUrlRelation>>(ReleaseGroupRels.GetValueOrDefault(mbid) ?? []);
    public Task<IReadOnlyList<MusicBrainzUrlRelation>> GetReleaseUrlRelationsAsync(string mbid, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MusicBrainzUrlRelation>>(ReleaseRels.GetValueOrDefault(mbid) ?? []);
}

internal sealed class FakeIdentityDiscogsClient : IDiscogsClient
{
    public bool IsEnabled { get; set; } = true;
    public bool IsAuthenticated { get; set; } = true;
    public DiscogsRateLimitSnapshot? LastRateLimit { get; set; }
    public long RequestsSent { get; set; }

    public List<DiscogsSearchResult> SearchResults { get; set; } = [];
    public Dictionary<long, DiscogsDocument<DiscogsRelease>> Releases { get; } = new();
    public Dictionary<long, DiscogsDocument<DiscogsMaster>> Masters { get; } = new();
    public Dictionary<long, DiscogsPage<DiscogsArtistReleaseEntry>> ArtistReleases { get; } = new();
    public Func<DiscogsSearchQuery, DiscogsPage<DiscogsSearchResult>>? SearchHandler { get; set; }
    public Exception? ThrowOnSearch { get; set; }

    public Task<DiscogsDocument<DiscogsArtist>?> GetArtistAsync(long id, CancellationToken ct = default) =>
        Task.FromResult<DiscogsDocument<DiscogsArtist>?>(null);
    public Task<DiscogsDocument<DiscogsLabel>?> GetLabelAsync(long id, CancellationToken ct = default) =>
        Task.FromResult<DiscogsDocument<DiscogsLabel>?>(null);
    public Task<DiscogsDocument<DiscogsMaster>?> GetMasterAsync(long id, CancellationToken ct = default) =>
        Task.FromResult(Masters.GetValueOrDefault(id));
    public Task<DiscogsDocument<DiscogsRelease>?> GetReleaseAsync(long id, CancellationToken ct = default) =>
        Task.FromResult(Releases.GetValueOrDefault(id));
    public Task<DiscogsPage<DiscogsArtistReleaseEntry>?> GetArtistReleasesAsync(long id, int page = 1, int perPage = 100, CancellationToken ct = default) =>
        Task.FromResult(ArtistReleases.GetValueOrDefault(id));
    public Task<DiscogsPage<DiscogsLabelReleaseEntry>?> GetLabelReleasesAsync(long id, int page = 1, int perPage = 100, CancellationToken ct = default) =>
        Task.FromResult<DiscogsPage<DiscogsLabelReleaseEntry>?>(null);
    public Task<DiscogsPage<DiscogsMasterVersion>?> GetMasterVersionsAsync(long id, int page = 1, int perPage = 100, DiscogsVersionFilter? filter = null, CancellationToken ct = default) =>
        Task.FromResult<DiscogsPage<DiscogsMasterVersion>?>(null);

    public Task<DiscogsPage<DiscogsSearchResult>> SearchAsync(DiscogsSearchQuery query, int page = 1, int perPage = 25, CancellationToken ct = default)
    {
        RequestsSent++;
        if (ThrowOnSearch is not null)
            throw ThrowOnSearch;
        if (SearchHandler is not null)
            return Task.FromResult(SearchHandler(query));
        return Task.FromResult(new DiscogsPage<DiscogsSearchResult>(
            new DiscogsPagination(page, 1, perPage, SearchResults.Count), SearchResults));
    }

    public Task<DiscogsImageBytes?> DownloadImageAsync(string signedUrl, CancellationToken ct = default) =>
        Task.FromResult<DiscogsImageBytes?>(null);
}
