using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Library;

namespace MusicHoarder.Api.Tests.Library;

public class DiscogsGenreLookupTests
{
    [Fact]
    public async Task NotFound_CachesNullAndDoesNotPause()
    {
        var mbid = Guid.NewGuid();
        var calls = 0;
        var handler = new CountingHandler(HttpStatusCode.NotFound, () => calls++);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var musicBrainz = new StubMb { DiscogsId = "42" };
        var lookup = new DiscogsGenreLookup(
            musicBrainz,
            new HttpClient(handler) { BaseAddress = new Uri("https://api.discogs.com/") },
            cache,
            NullLogger<DiscogsGenreLookup>.Instance,
            TimeProvider.System);

        Assert.Null(await lookup.FindAsync(mbid, default));
        Assert.Null(await lookup.FindAsync(mbid, default));

        Assert.Equal(1, calls);
        Assert.Equal(1, musicBrainz.Calls);
    }

    private sealed class StubMb : IMusicBrainzWebService
    {
        public string? DiscogsId { get; set; }
        public int Calls { get; private set; }

        public Task<string?> DiscogsReleaseIdAsync(Guid release, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(DiscogsId);
        }

        public Task<MusicBrainzRecording?> LookupByRecordingIdAsync(string mbid, CancellationToken ct = default)
            => Task.FromResult<MusicBrainzRecording?>(null);
        public Task<MusicBrainzRecording?> LookupByIsrcAsync(string isrc, CancellationToken ct = default)
            => Task.FromResult<MusicBrainzRecording?>(null);
        public Task<IReadOnlyList<MusicBrainzRecording>> SearchAsync(
            string artist, string title, int limit, string? album = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MusicBrainzRecording>>([]);
        public Task<IReadOnlyList<MusicBrainzRecording>> SearchFreeTextAsync(
            string query, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MusicBrainzRecording>>([]);
        public Task<MusicBrainzRelease?> LookupReleaseAsync(string releaseId, CancellationToken ct = default)
            => Task.FromResult<MusicBrainzRelease?>(null);
        public Task<IReadOnlyList<MusicBrainzReleaseSearchResult>> SearchReleasesAsync(
            string artist, string album, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MusicBrainzReleaseSearchResult>>([]);
    }

    private sealed class CountingHandler(HttpStatusCode status, Action onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
