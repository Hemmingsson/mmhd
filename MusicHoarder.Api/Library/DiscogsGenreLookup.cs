using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.RateLimiting;

namespace MusicHoarder.Api.Library;

public sealed record DiscogsArtistForTags(string Name);
public sealed record DiscogsFormat(string[]? Descriptions);
public sealed record DiscogsReleaseForTags(long Id, string Title, DiscogsArtistForTags[] Artists,
    string[] Genres, string[] Styles, DiscogsFormat[]? Formats)
{
    public bool Compilation => Formats?.Any(f => f.Descriptions?.Contains("Compilation", StringComparer.OrdinalIgnoreCase) == true) == true
        || Artists.Any(a => a.Name == "Various");

    public bool Matches(AlbumIdentity album) =>
        string.Equals(Title, album.Album, StringComparison.OrdinalIgnoreCase)
        && Artists is { Length: 1 } && (string.Equals(Artists[0].Name, album.AlbumArtist, StringComparison.OrdinalIgnoreCase)
            || (album.IsCompilation && Artists[0].Name == "Various"));
}

public sealed class DiscogsGenreLookup(
    IMusicBrainzWebService musicBrainz,
    HttpClient client,
    IMemoryCache cache,
    ILogger<DiscogsGenreLookup> logger,
    TimeProvider time)
{
    private static readonly ReconfigurableRateLimiter RateLimiter = new();
    private static DateTimeOffset pausedUntil;
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan DefaultPause = TimeSpan.FromMinutes(1);

    // MemoryCache rejects null values; wrap so a miss is still a cache hit.
    internal sealed record CacheEntry(DiscogsReleaseForTags? Release);

    public async Task<DiscogsReleaseForTags?> FindAsync(Guid mbid, CancellationToken ct)
    {
        var key = $"discogs-release:{mbid:D}";
        if (cache.TryGetValue(key, out CacheEntry? hit)) return hit!.Release;
        if (time.GetUtcNow() < pausedUntil) return null;

        using var lease = await RateLimiter.AcquireAsync(1, TimeSpan.FromSeconds(3), ct);

        if (cache.TryGetValue(key, out hit)) return hit!.Release;
        if (time.GetUtcNow() < pausedUntil) return null;

        try
        {
            var id = await musicBrainz.DiscogsReleaseIdAsync(mbid, ct);
            DiscogsReleaseForTags? release = null;
            if (long.TryParse(id, out var expected))
            {
                using var response = await client.GetAsync($"releases/{expected}", ct);
                var now = time.GetUtcNow();
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    pausedUntil = now.Add(RetryAfterHeader.Read(response, now) ?? DefaultPause);
                    return null;
                }

                if ((int)response.StatusCode is >= 400 and < 500)
                {
                    // 404 / other client errors are release-local — cache a miss, do not pause everyone.
                    Cache(key, null);
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    pausedUntil = now.Add(DefaultPause);
                    return null;
                }

                release = await response.Content.ReadFromJsonAsync<DiscogsReleaseForTags>(cancellationToken: ct);
                if (release is null || release.Id != expected || release.Artists is null)
                    release = null;
                else
                    release = release with
                    {
                        Genres = release.Genres ?? [],
                        Styles = release.Styles ?? [],
                    };
            }

            Cache(key, release);
            return release;
        }
        catch (ProviderRateLimitedException ex)
        {
            pausedUntil = time.GetUtcNow().Add(ex.RetryAfter);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Discogs genre lookup unavailable for release {Release}", mbid);
            pausedUntil = time.GetUtcNow().Add(DefaultPause);
            return null;
        }
    }

    private void Cache(string key, DiscogsReleaseForTags? release) =>
        cache.Set(key, new CacheEntry(release), release is null ? NegativeTtl : PositiveTtl);
}
