using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.MusicBrainz;

public class MusicBrainzUrlRelationTests
{
    [Fact]
    public async Task GetArtistUrlRelationsAsync_ParsesDiscogsFromFixture()
    {
        var json = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Discogs", "musicbrainz-artist-url-rels.json"));
        var service = CreateService(new StubHandler(json));

        var rels = await service.GetArtistUrlRelationsAsync("f22942a1-6f70-4f48-866e-238cb2308fbd");

        Assert.Contains(rels, r =>
            string.Equals(r.Type, "discogs", StringComparison.OrdinalIgnoreCase)
            && r.Url.Contains("discogs.com/artist/45", StringComparison.OrdinalIgnoreCase));
        Assert.True(rels.Count > 1);
    }

    [Fact]
    public async Task GetReleaseGroupUrlRelationsAsync_ParsesDiscogsMasterFromFixture()
    {
        var json = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Discogs", "musicbrainz-release-group-url-rels.json"));
        var service = CreateService(new StubHandler(json));

        var rels = await service.GetReleaseGroupUrlRelationsAsync("aaaa");

        Assert.Contains(rels, r =>
            string.Equals(r.Type, "discogs", StringComparison.OrdinalIgnoreCase)
            && r.Url.Contains("discogs.com/master/565", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetReleaseUrlRelationsAsync_EmptyBody_ReturnsEmpty()
    {
        var service = CreateService(new StubHandler("""{"relations":[]}"""));
        var rels = await service.GetReleaseUrlRelationsAsync("bbbb");
        Assert.Empty(rels);
    }

    [Fact]
    public async Task DefaultInterfaceMethod_ReturnsEmptyWithoutOverride()
    {
        IMusicBrainzWebService stub = new EmptyMusicBrainz();
        Assert.Empty(await stub.GetArtistUrlRelationsAsync("x"));
        Assert.Empty(await stub.GetReleaseGroupUrlRelationsAsync("x"));
        Assert.Empty(await stub.GetReleaseUrlRelationsAsync("x"));
    }

    private static MusicBrainzWebService CreateService(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://musicbrainz.org/ws/2/") };
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
        });
        return new MusicBrainzWebService(http, options, NullLogger<MusicBrainzWebService>.Instance);
    }

    private sealed class StubHandler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class EmptyMusicBrainz : IMusicBrainzWebService
    {
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
    }
}
