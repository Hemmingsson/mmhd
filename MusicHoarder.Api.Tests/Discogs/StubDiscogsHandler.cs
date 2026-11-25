using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace MusicHoarder.Api.Tests.Discogs;

/// <summary>
/// Test double for Discogs HTTP: queued responses and/or path-prefix routes, with request recording.
/// </summary>
internal sealed class StubDiscogsHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _queued = new();
    private readonly List<(string Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];
    private readonly object _gate = new();

    public List<RecordedRequest> Requests { get; } = [];

    public int SendCount
    {
        get
        {
            lock (_gate) return Requests.Count;
        }
    }

    /// <summary>Enqueue a fixed response for the next unmatched request (FIFO).</summary>
    public void Enqueue(HttpStatusCode status, string body = "", Action<HttpResponseMessage>? configure = null) =>
        _queued.Enqueue(_ => Create(status, body, configure));

    public void EnqueueJson(string json, Action<HttpResponseMessage>? configure = null) =>
        Enqueue(HttpStatusCode.OK, json, configure);

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        _queued.Enqueue(respond);

    /// <summary>
    /// Register a route matched when the request URL contains <paramref name="pathContains"/>
    /// (OrdinalIgnoreCase). Routes are tried before the FIFO queue.
    /// </summary>
    public void Map(string pathContains, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        lock (_gate)
            _routes.Add((pathContains, respond));
    }

    public void MapJson(string pathContains, string json, Action<HttpResponseMessage>? configure = null) =>
        Map(pathContains, _ => Create(HttpStatusCode.OK, json, configure));

    public void MapStatus(string pathContains, HttpStatusCode status, string body = "", Action<HttpResponseMessage>? configure = null) =>
        Map(pathContains, _ => Create(status, body, configure));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // AbsoluteUri keeps %20 / %26 encodings; Uri.ToString() partially unescapes and breaks asserts.
        var url = request.RequestUri?.AbsoluteUri
            ?? request.RequestUri?.OriginalString
            ?? request.RequestUri?.ToString()
            ?? "";
        var recorded = new RecordedRequest(
            request.Method,
            url,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            request.Headers.UserAgent.ToString(),
            request.Headers.TryGetValues("User-Agent", out var ua) ? string.Join(" ", ua) : null,
            body);

        lock (_gate)
            Requests.Add(recorded);

        Func<HttpRequestMessage, HttpResponseMessage>? respond = null;

        lock (_gate)
        {
            foreach (var (match, handler) in _routes)
            {
                if (url.Contains(match, StringComparison.OrdinalIgnoreCase))
                {
                    respond = handler;
                    break;
                }
            }
        }

        if (respond is null && _queued.TryDequeue(out var queued))
            respond = queued;

        if (respond is null)
            throw new InvalidOperationException($"No stubbed Discogs response for {request.Method} {url}");

        return respond(request);
    }

    public static HttpResponseMessage Create(
        HttpStatusCode status,
        string body = "",
        Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        configure?.Invoke(response);
        return response;
    }

    public static void WithRateLimit(HttpResponseMessage response, int limit, int used, int remaining)
    {
        response.Headers.TryAddWithoutValidation("X-Discogs-Ratelimit", limit.ToString());
        response.Headers.TryAddWithoutValidation("X-Discogs-Ratelimit-Used", used.ToString());
        response.Headers.TryAddWithoutValidation("X-Discogs-Ratelimit-Remaining", remaining.ToString());
    }

    public static void WithRetryAfter(HttpResponseMessage response, TimeSpan delay) =>
        response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);

    public static void WithRetryAfter(HttpResponseMessage response, DateTimeOffset when) =>
        response.Headers.RetryAfter = new RetryConditionHeaderValue(when);
}

internal sealed record RecordedRequest(
    HttpMethod Method,
    string Url,
    string? AuthScheme,
    string? AuthParameter,
    string UserAgentHeader,
    string? UserAgentRaw,
    string? Body);
