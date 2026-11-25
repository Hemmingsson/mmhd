using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using MusicHoarder.Api.Enrichment;

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

public sealed class DiscogsGenreLookup(IMusicBrainzWebService musicBrainz, HttpClient client,
    IMemoryCache cache, ILogger<DiscogsGenreLookup> logger)
{
    private readonly SemaphoreSlim gate = new(1);
    private DateTimeOffset nextRequest;
    private DateTimeOffset pausedUntil;

    public async Task<DiscogsReleaseForTags?> FindAsync(Guid mbid, CancellationToken ct)
    {
        var key = $"discogs-release:{mbid:D}";
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out DiscogsReleaseForTags? cached)) return cached;
            if (DateTimeOffset.UtcNow < pausedUntil) return null;
            var id = await musicBrainz.DiscogsReleaseIdAsync(mbid, ct);
            DiscogsReleaseForTags? release = null;
            if (long.TryParse(id, out var expected))
            {
                var delay = nextRequest - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
                nextRequest = DateTimeOffset.UtcNow.AddSeconds(3);
                using var response = await client.GetAsync($"releases/{expected}", ct);
                if ((int)response.StatusCode == 429 || (int)response.StatusCode == 503)
                {
                    var retry = response.Headers.RetryAfter;
                    pausedUntil = retry?.Date ?? DateTimeOffset.UtcNow.Add(retry?.Delta ?? TimeSpan.FromMinutes(1));
                    return null;
                }
                response.EnsureSuccessStatusCode();
                release = await response.Content.ReadFromJsonAsync<DiscogsReleaseForTags>(cancellationToken: ct);
                if (release?.Id != expected || release.Genres is null || release.Styles is null || release.Artists is null)
                    return null;
            }
            cache.Set(key, release, release is null ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(24));
            return release;
        }
        catch (ProviderRateLimitedException ex)
        {
            pausedUntil = DateTimeOffset.UtcNow.Add(ex.RetryAfter);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Discogs genre lookup unavailable for release {Release}", mbid);
            pausedUntil = DateTimeOffset.UtcNow.AddMinutes(1);
            return null;
        }
        finally { gate.Release(); }
    }
}
