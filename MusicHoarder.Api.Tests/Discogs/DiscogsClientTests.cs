using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Discogs;

public class DiscogsClientTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Sends_user_agent_and_authorization_when_token_set()
    {
        var stub = new StubDiscogsHandler();
        stub.EnqueueJson(DiscogsContractTests.Fixture("artist-45-aphex-twin.json"));
        var client = CreateClient(stub, o => o.Token = "secret-token");

        await client.GetArtistAsync(45);

        var req = Assert.Single(stub.Requests);
        Assert.Equal("Discogs", req.AuthScheme);
        Assert.Equal("token=secret-token", req.AuthParameter);
        Assert.Contains("MusicHoarder", req.UserAgentRaw ?? req.UserAgentHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", req.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Omits_authorization_when_token_absent()
    {
        var stub = new StubDiscogsHandler();
        stub.EnqueueJson(DiscogsContractTests.Fixture("artist-45-aphex-twin.json"));
        var client = CreateClient(stub, o => o.Token = "");

        await client.GetArtistAsync(45);

        var req = Assert.Single(stub.Requests);
        Assert.Null(req.AuthScheme);
        Assert.Null(req.AuthParameter);
    }

    [Fact]
    public async Task GetArtist_maps_fixture_and_returns_raw_json()
    {
        var raw = DiscogsContractTests.Fixture("artist-45-aphex-twin.json");
        var stub = new StubDiscogsHandler();
        stub.EnqueueJson(raw);
        var client = CreateClient(stub);

        var doc = await client.GetArtistAsync(45);

        Assert.NotNull(doc);
        Assert.Equal(45, doc.Value.Id);
        Assert.Equal("Aphex Twin", doc.Value.Name);
        Assert.Equal(raw, doc.RawJson);
    }

    [Fact]
    public async Task GetLabel_maps_fixture()
    {
        var stub = new StubDiscogsHandler();
        stub.EnqueueJson(DiscogsContractTests.Fixture("label-23528-warp.json"));
        var doc = await CreateClient(stub).GetLabelAsync(23528);
        Assert.Equal("Warp Records", doc!.Value.Name);
    }

    [Fact]
    public async Task GetMaster_maps_fixture()
    {
        var stub = new StubDiscogsHandler();
        stub.EnqueueJson(DiscogsContractTests.Fixture("master-565.json"));
        var doc = await CreateClient(stub).GetMasterAsync(565);
        Assert.Equal("Selected Ambient Works 85-92", doc!.Value.Title);
    }

    [Fact]
    public async Task GetRelease_maps_fixture()
    {
        var stub = new StubDiscogsHandler();
        stub.EnqueueJson(DiscogsContractTests.Fixture("release-249504-vinyl-single.json"));
        var doc = await CreateClient(stub).GetReleaseAsync(249504);
        Assert.Equal(249504, doc!.Value.Id);
        Assert.Equal(1987, doc.Value.Year);
    }

    [Fact]
    public async Task Get_returns_null_on_404()
    {
        var stub = new StubDiscogsHandler();
        stub.Enqueue(HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        Assert.Null(await CreateClient(stub).GetArtistAsync(999));
        Assert.Equal(1, stub.SendCount);
    }

    [Fact]
    public async Task Caches_successful_document_so_second_call_makes_no_request()
    {
        var stub = new StubDiscogsHandler();
        stub.EnqueueJson(DiscogsContractTests.Fixture("artist-45-aphex-twin.json"));
        var client = CreateClient(stub, o => o.CacheMinutes = 60);

        var first = await client.GetArtistAsync(45);
        var second = await client.GetArtistAsync(45);

        Assert.Same(first, second);
        Assert.Equal(1, stub.SendCount);
    }

    [Fact]
    public async Task Artist_releases_paginate_with_sort()
    {
        var stub = new StubDiscogsHandler();
        stub.EnqueueJson(DiscogsContractTests.Fixture("artist-45-releases-page1.json"));
        var page = await CreateClient(stub).GetArtistReleasesAsync(45, page: 1, perPage: 5);

        Assert.NotNull(page);
        Assert.Equal(1, page.Pagination.Page);
        Assert.Equal(265, page.Pagination.Pages);
        Assert.Equal(1323, page.Pagination.Items);
        Assert.Contains(page.Items, r => r.Id == 565 && r.Title!.Contains("Ambient", StringComparison.Ordinal));
        Assert.Contains("sort=year", stub.Requests[0].Url, StringComparison.Ordinal);
        Assert.Contains("sort_order=asc", stub.Requests[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Label_releases_paginate()
    {
        var stub = new StubDiscogsHandler();
        stub.EnqueueJson(DiscogsContractTests.Fixture("label-23528-releases-page1.json"));
        var page = await CreateClient(stub).GetLabelReleasesAsync(23528, page: 1, perPage: 5);

        Assert.NotNull(page);
        Assert.NotEmpty(page.Items);
        Assert.Contains("/labels/23528/releases", stub.Requests[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Master_versions_send_filter_params()
    {
        var stub = new StubDiscogsHandler();
        stub.EnqueueJson(DiscogsContractTests.Fixture("master-565-versions-page1.json"));
        var filter = new DiscogsVersionFilter(Format: "CD", Label: "R & S Records", Released: "1992", Country: "UK");
        var page = await CreateClient(stub).GetMasterVersionsAsync(565, page: 1, perPage: 3, filter: filter);

        Assert.NotNull(page);
        Assert.NotEmpty(page.Items);
        var url = stub.Requests[0].Url;
        Assert.Contains("format=CD", url, StringComparison.Ordinal);
        Assert.Contains("label=R%20%26%20S%20Records", url, StringComparison.Ordinal);
        Assert.Contains("released=1992", url, StringComparison.Ordinal);
        Assert.Contains("country=UK", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_builds_encoded_query_string()
    {
        var stub = new StubDiscogsHandler();
        stub.EnqueueJson(DiscogsContractTests.Fixture("search-release-saw-85-92.json"));
        var query = new DiscogsSearchQuery(
            Type: "release",
            Artist: "Aphex Twin",
            ReleaseTitle: "Selected Ambient Works 85-92",
            Query: "a & b");
        var page = await CreateClient(stub, o => o.Token = "tok").SearchAsync(query, page: 1, perPage: 3);

        Assert.NotEmpty(page.Items);
        var url = stub.Requests[0].Url;
        Assert.Contains("/database/search?", url, StringComparison.Ordinal);
        Assert.Contains("type=release", url, StringComparison.Ordinal);
        Assert.Contains("artist=Aphex%20Twin", url, StringComparison.Ordinal);
        Assert.Contains("release_title=Selected%20Ambient%20Works%2085-92", url, StringComparison.Ordinal);
        Assert.Contains("q=a%20%26%20b", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_without_token_returns_empty_and_makes_no_request()
    {
        var stub = new StubDiscogsHandler();
        var page = await CreateClient(stub, o => o.Token = "").SearchAsync(new DiscogsSearchQuery(Query: "x"));

        Assert.Empty(page.Items);
        Assert.Equal(1, page.Pagination.Page);
        Assert.Equal(0, page.Pagination.Pages);
        Assert.Equal(0, page.Pagination.Items);
        Assert.Equal(0, stub.SendCount);
    }

    [Fact]
    public async Task Parses_rate_limit_headers_into_LastRateLimit()
    {
        var stub = new StubDiscogsHandler();
        stub.Enqueue(HttpStatusCode.OK, DiscogsContractTests.Fixture("artist-45-aphex-twin.json"), r =>
            StubDiscogsHandler.WithRateLimit(r, limit: 60, used: 12, remaining: 48));
        var clock = new ImmediateTimeProvider(Start);
        var client = CreateClient(stub, time: clock);

        await client.GetArtistAsync(45);

        Assert.NotNull(client.LastRateLimit);
        Assert.Equal(60, client.LastRateLimit.Limit);
        Assert.Equal(12, client.LastRateLimit.Used);
        Assert.Equal(48, client.LastRateLimit.Remaining);
        Assert.Equal(Start.UtcDateTime, client.LastRateLimit.ObservedAtUtc);
    }

    [Fact]
    public async Task Retry_after_429_then_200_succeeds_and_counts_retry()
    {
        var stub = new StubDiscogsHandler();
        stub.Enqueue(HttpStatusCode.TooManyRequests, "", r => StubDiscogsHandler.WithRetryAfter(r, TimeSpan.FromSeconds(1)));
        stub.EnqueueJson(DiscogsContractTests.Fixture("artist-45-aphex-twin.json"));
        var metrics = CreateMetrics();
        var client = CreateClient(stub, metrics: metrics);

        var doc = await client.GetArtistAsync(45);

        Assert.Equal(45, doc!.Value.Id);
        Assert.Equal(2, stub.SendCount);
    }

    [Fact]
    public async Task Exhausted_429_retries_throw_ProviderRateLimitedException()
    {
        var stub = new StubDiscogsHandler();
        stub.Enqueue(HttpStatusCode.TooManyRequests, "", r => StubDiscogsHandler.WithRetryAfter(r, TimeSpan.FromSeconds(5)));
        stub.Enqueue(HttpStatusCode.TooManyRequests, "", r => StubDiscogsHandler.WithRetryAfter(r, TimeSpan.FromSeconds(5)));
        var client = CreateClient(stub, o =>
        {
            o.MaxRetries = 1;
            o.MaxRetryWaitSeconds = 90;
        });

        var ex = await Assert.ThrowsAsync<ProviderRateLimitedException>(() => client.GetArtistAsync(45));
        Assert.Equal(TimeSpan.FromSeconds(5), ex.RetryAfter);
        Assert.Equal(2, stub.SendCount);
    }

    [Fact]
    public async Task Retries_503_then_succeeds()
    {
        var stub = new StubDiscogsHandler();
        stub.Enqueue(HttpStatusCode.ServiceUnavailable, "down");
        stub.EnqueueJson(DiscogsContractTests.Fixture("artist-45-aphex-twin.json"));
        var doc = await CreateClient(stub).GetArtistAsync(45);
        Assert.Equal(45, doc!.Value.Id);
        Assert.Equal(2, stub.SendCount);
    }

    [Fact]
    public async Task Unauthorized_throws_DiscogsApiException_without_retry()
    {
        var stub = new StubDiscogsHandler();
        stub.Enqueue(HttpStatusCode.Unauthorized, "nope");
        stub.EnqueueJson(DiscogsContractTests.Fixture("artist-45-aphex-twin.json"));

        var ex = await Assert.ThrowsAsync<DiscogsApiException>(() => CreateClient(stub).GetArtistAsync(45));
        Assert.Equal(401, ex.StatusCode);
        Assert.Equal(1, stub.SendCount);
    }

    [Fact]
    public async Task Disabled_client_makes_no_request()
    {
        var stub = new StubDiscogsHandler();
        var client = CreateClient(stub, o => o.Enabled = false);

        Assert.Null(await client.GetArtistAsync(45));
        Assert.Null(await client.GetArtistReleasesAsync(45));
        var search = await client.SearchAsync(new DiscogsSearchQuery(Query: "x"));
        Assert.Empty(search.Items);
        Assert.Null(await client.DownloadImageAsync("https://i.discogs.com/x.jpg"));
        Assert.Equal(0, stub.SendCount);
    }

    [Fact]
    public async Task DownloadImage_refuses_non_discogs_host()
    {
        var stub = new StubDiscogsHandler();
        var client = CreateClient(stub, o => o.Token = "tok");
        Assert.Null(await client.DownloadImageAsync("https://evil.example/img.jpg"));
        Assert.Equal(0, stub.SendCount);
    }

    [Fact]
    public async Task DownloadImage_works_for_i_discogs_com()
    {
        var stub = new StubDiscogsHandler();
        stub.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4])
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") },
                },
            };
            return response;
        });
        var url = "https://i.discogs.com/abc/image.jpeg?sig=1";
        var result = await CreateClient(stub, o => o.Token = "tok").DownloadImageAsync(url);

        Assert.NotNull(result);
        Assert.Equal([1, 2, 3, 4], result.Content);
        Assert.Contains("image/jpeg", result.ContentType, StringComparison.Ordinal);
        Assert.Equal(url, stub.Requests[0].Url);
    }

    [Fact]
    public async Task Concurrent_requests_with_MaxConcurrency_1_do_not_overlap()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0;
        var maxInFlight = 0;
        var responses = new ConcurrentQueue<string>(
        [
            DiscogsContractTests.Fixture("artist-45-aphex-twin.json"),
            DiscogsContractTests.Fixture("label-23528-warp.json"),
        ]);

        var handler = new AsyncStubHandler(async _ =>
        {
            var now = Interlocked.Increment(ref inFlight);
            UpdateMax(ref maxInFlight, now);
            firstEntered.TrySetResult();
            await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Interlocked.Decrement(ref inFlight);
            Assert.True(responses.TryDequeue(out var body));
            return StubDiscogsHandler.Create(HttpStatusCode.OK, body);
        });

        var client = CreateClient(handler, o =>
        {
            o.MaxConcurrency = 1;
            o.RequestsPerMinute = 60;
            o.CacheMinutes = 0;
        });

        var t1 = client.GetArtistAsync(45);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var t2 = client.GetLabelAsync(23528);

        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref maxInFlight));
        Assert.False(t2.IsCompleted);

        releaseFirst.TrySetResult();
        await Task.WhenAll(t1, t2);

        Assert.Equal(1, maxInFlight);
        Assert.Equal(2, handler.SendCount);
    }

    private static void UpdateMax(ref int maxInFlight, int now)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref maxInFlight);
            if (now <= snapshot || Interlocked.CompareExchange(ref maxInFlight, now, snapshot) == snapshot)
                return;
        }
    }

    /// <summary>Handler whose callback may await — used to hold a request in flight without blocking a thread.</summary>
    private sealed class AsyncStubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return await respond(request).ConfigureAwait(false);
        }
    }

    private static DiscogsClient CreateClient(
        HttpMessageHandler stub,
        Action<DiscogsOptions>? configure = null,
        TimeProvider? time = null,
        DiscogsMetrics? metrics = null)
    {
        var options = new DiscogsOptions
        {
            Enabled = true,
            Token = "test-token",
            UserAgent = "MusicHoarder-Test/1.0",
            BaseUrl = "https://api.discogs.com/",
            RequestsPerMinute = 60,
            MaxConcurrency = 2,
            MaxRetries = 3,
            MaxRetryWaitSeconds = 90,
            CacheMinutes = 0,
        };
        configure?.Invoke(options);

        var http = new HttpClient(stub) { BaseAddress = new Uri(options.BaseUrl) };
        return new DiscogsClient(
            http,
            new StaticOptionsMonitor<DiscogsOptions>(options),
            new MemoryCache(new MemoryCacheOptions()),
            metrics ?? CreateMetrics(),
            time ?? new ImmediateTimeProvider(Start),
            NullLogger<DiscogsClient>.Instance);
    }

    private static DiscogsMetrics CreateMetrics()
    {
        var meterFactory = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();
        return new DiscogsMetrics(meterFactory);
    }

    /// <summary>Completes <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> immediately.</summary>
    private sealed class ImmediateTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new ImmediateTimer(callback, state, dueTime);
    }

    private sealed class ImmediateTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private int _fired;

        public ImmediateTimer(TimerCallback callback, object? state, TimeSpan dueTime)
        {
            _callback = callback;
            _state = state;
            if (dueTime != Timeout.InfiniteTimeSpan)
                ThreadPool.QueueUserWorkItem(_ => Fire());
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == Timeout.InfiniteTimeSpan)
                return true;
            ThreadPool.QueueUserWorkItem(_ => Fire());
            return true;
        }

        private void Fire()
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0)
                _callback(_state);
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
