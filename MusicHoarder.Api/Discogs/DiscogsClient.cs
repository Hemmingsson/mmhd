using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.RateLimiting;

namespace MusicHoarder.Api.Discogs;

/// <summary>
/// HTTP client for the Discogs database API: document GETs, paginated lists, search, and signed
/// image downloads, with caching, concurrency/spacing, and 429/5xx retries.
/// </summary>
public sealed class DiscogsClient : IDiscogsClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<DiscogsOptions> _options;
    private readonly IMemoryCache _cache;
    private readonly DiscogsMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly ILogger<DiscogsClient> _logger;
    private readonly DiscogsRequestGate _gate;

    public DiscogsClient(
        HttpClient httpClient,
        IOptionsMonitor<DiscogsOptions> options,
        IMemoryCache cache,
        DiscogsMetrics metrics,
        TimeProvider time,
        ILogger<DiscogsClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _cache = cache;
        _metrics = metrics;
        _time = time;
        _logger = logger;
        _gate = new DiscogsRequestGate(time);
        _metrics.RemainingProvider = () => LastRateLimit?.Remaining;
    }

    public bool IsEnabled => _options.CurrentValue.Enabled;

    public bool IsAuthenticated
    {
        get
        {
            var o = _options.CurrentValue;
            return o.Enabled && o.HasToken;
        }
    }

    public DiscogsRateLimitSnapshot? LastRateLimit { get; private set; }

    public Task<DiscogsDocument<DiscogsArtist>?> GetArtistAsync(long id, CancellationToken ct = default) =>
        GetDocumentAsync<DiscogsArtist>(DiscogsResourceKind.Artist, "artists", id, "artist", ct);

    public Task<DiscogsDocument<DiscogsLabel>?> GetLabelAsync(long id, CancellationToken ct = default) =>
        GetDocumentAsync<DiscogsLabel>(DiscogsResourceKind.Label, "labels", id, "label", ct);

    public Task<DiscogsDocument<DiscogsMaster>?> GetMasterAsync(long id, CancellationToken ct = default) =>
        GetDocumentAsync<DiscogsMaster>(DiscogsResourceKind.Master, "masters", id, "master", ct);

    public Task<DiscogsDocument<DiscogsRelease>?> GetReleaseAsync(long id, CancellationToken ct = default) =>
        GetDocumentAsync<DiscogsRelease>(DiscogsResourceKind.Release, "releases", id, "release", ct);

    public async Task<DiscogsPage<DiscogsArtistReleaseEntry>?> GetArtistReleasesAsync(
        long id, int page = 1, int perPage = 100, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return null;

        (page, perPage) = ClampPaging(page, perPage);
        var path = $"artists/{id}/releases?page={page}&per_page={perPage}&sort=year&sort_order=asc";
        return await GetPageAsync<DiscogsArtistReleaseEntry>(path, "releases", "artist_releases", ct).ConfigureAwait(false);
    }

    public async Task<DiscogsPage<DiscogsLabelReleaseEntry>?> GetLabelReleasesAsync(
        long id, int page = 1, int perPage = 100, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return null;

        (page, perPage) = ClampPaging(page, perPage);
        var path = $"labels/{id}/releases?page={page}&per_page={perPage}";
        return await GetPageAsync<DiscogsLabelReleaseEntry>(path, "releases", "label_releases", ct).ConfigureAwait(false);
    }

    public async Task<DiscogsPage<DiscogsMasterVersion>?> GetMasterVersionsAsync(
        long id, int page = 1, int perPage = 100, DiscogsVersionFilter? filter = null, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return null;

        (page, perPage) = ClampPaging(page, perPage);
        var qs = new StringBuilder($"page={page}&per_page={perPage}");
        AppendQuery(qs, "format", filter?.Format);
        AppendQuery(qs, "label", filter?.Label);
        AppendQuery(qs, "released", filter?.Released);
        AppendQuery(qs, "country", filter?.Country);
        var path = $"masters/{id}/versions?{qs}";
        return await GetPageAsync<DiscogsMasterVersion>(path, "versions", "master_versions", ct).ConfigureAwait(false);
    }

    public async Task<DiscogsPage<DiscogsSearchResult>> SearchAsync(
        DiscogsSearchQuery query, int page = 1, int perPage = 25, CancellationToken ct = default)
    {
        (page, perPage) = ClampPaging(page, perPage);

        if (!IsEnabled || !IsAuthenticated)
            return EmptySearchPage(perPage);

        var qs = new StringBuilder($"page={page}&per_page={perPage}");
        AppendQuery(qs, "type", query.Type);
        AppendQuery(qs, "q", query.Query);
        AppendQuery(qs, "title", query.Title);
        AppendQuery(qs, "release_title", query.ReleaseTitle);
        AppendQuery(qs, "artist", query.Artist);
        AppendQuery(qs, "anv", query.Anv);
        AppendQuery(qs, "credit", query.Credit);
        AppendQuery(qs, "label", query.Label);
        AppendQuery(qs, "genre", query.Genre);
        AppendQuery(qs, "style", query.Style);
        AppendQuery(qs, "country", query.Country);
        if (query.Year is int year)
            AppendQuery(qs, "year", year.ToString(CultureInfo.InvariantCulture));
        AppendQuery(qs, "format", query.Format);
        AppendQuery(qs, "catno", query.Catno);
        AppendQuery(qs, "barcode", query.Barcode);
        AppendQuery(qs, "track", query.Track);

        var pageResult = await GetPageAsync<DiscogsSearchResult>(
            $"database/search?{qs}", "results", "search", ct).ConfigureAwait(false);
        return pageResult ?? EmptySearchPage(perPage);
    }

    public async Task<DiscogsImageBytes?> DownloadImageAsync(string signedUrl, CancellationToken ct = default)
    {
        if (!IsEnabled || !IsAuthenticated)
            return null;

        if (!Uri.TryCreate(signedUrl, UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host.ToLowerInvariant();
        if (host != "discogs.com" && !host.EndsWith(".discogs.com", StringComparison.Ordinal))
            return null;

        // Never modify the URL — send it exactly as given.
        var response = await SendWithRetryAsync(uri, "image", ct).ConfigureAwait(false);
        if (response is null)
            return null;

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _metrics.RecordRequest("image", "not_found");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                // SendWithRetryAsync already threw for non-success that exhausted retries;
                // a success path that somehow lands here is defensive.
                _metrics.RecordRequest("image", "error");
                throw new DiscogsApiException(
                    $"Discogs image download failed with {(int)response.StatusCode}",
                    (int)response.StatusCode);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            _metrics.RecordRequest("image", "ok");
            return new DiscogsImageBytes(bytes, response.Content.Headers.ContentType?.ToString());
        }
    }

    private async Task<DiscogsDocument<T>?> GetDocumentAsync<T>(
        DiscogsResourceKind kind, string pathSegment, long id, string resourceTag, CancellationToken ct)
    {
        if (!IsEnabled)
            return null;

        var cacheKey = $"discogs:{kind}:{id}";
        var opts = _options.CurrentValue;
        if (opts.CacheMinutes > 0 && _cache.TryGetValue(cacheKey, out DiscogsDocument<T>? cached) && cached is not null)
            return cached;

        var response = await SendWithRetryAsync(BuildUri($"{pathSegment}/{id}"), resourceTag, ct).ConfigureAwait(false);
        if (response is null)
            return null;

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _metrics.RecordRequest(resourceTag, "not_found");
                return null;
            }

            EnsureSuccessOrThrow(response, resourceTag);
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var value = JsonSerializer.Deserialize<T>(raw, DiscogsJson.Options)
                ?? throw new DiscogsApiException($"Discogs {resourceTag} response could not be parsed.");
            var doc = new DiscogsDocument<T>(value, raw);

            if (opts.CacheMinutes > 0)
            {
                _cache.Set(cacheKey, doc, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(opts.CacheMinutes),
                });
            }

            _metrics.RecordRequest(resourceTag, "ok");
            return doc;
        }
    }

    private async Task<DiscogsPage<T>?> GetPageAsync<T>(
        string relativePath, string itemsProperty, string resourceTag, CancellationToken ct)
    {
        var response = await SendWithRetryAsync(BuildUri(relativePath), resourceTag, ct).ConfigureAwait(false);
        if (response is null)
            return null;

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _metrics.RecordRequest(resourceTag, "not_found");
                return null;
            }

            EnsureSuccessOrThrow(response, resourceTag);
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var page = ParsePage<T>(raw, itemsProperty);
            _metrics.RecordRequest(resourceTag, "ok");
            return page;
        }
    }

    /// <summary>
    /// Runs the gated GET with 429 / 5xx / transport retries. Returns the response on success or 404;
    /// throws on terminal failures. Caller owns disposing the response.
    /// </summary>
    private async Task<HttpResponseMessage?> SendWithRetryAsync(Uri uri, string resourceTag, CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        var maxRetries = Math.Max(0, opts.MaxRetries);

        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            opts = _options.CurrentValue;

            await _gate.AcquireAsync(opts.MaxConcurrency, opts.RequestsPerMinute, ct).ConfigureAwait(false);
            HttpResponseMessage? response = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                ApplyHeaders(request, opts);

                try
                {
                    response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    _gate.Release();
                    response = null;

                    if (attempt >= maxRetries)
                    {
                        _metrics.RecordRequest(resourceTag, "error");
                        throw new DiscogsApiException(
                            $"Discogs request failed: {ex.Message}",
                            statusCode: null,
                            inner: ex);
                    }

                    _metrics.RecordRetry();
                    var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 1, 2, 4…
                    _logger.LogWarning(ex,
                        "Discogs transport failure for {Resource}; retrying after {Delay}s (attempt {Attempt}/{Max})",
                        resourceTag, backoff.TotalSeconds, attempt + 1, maxRetries);
                    await Task.Delay(backoff, _time, ct).ConfigureAwait(false);
                    continue;
                }

                NoteRateLimitHeaders(response);

                var status = (int)response.StatusCode;

                if (status is 401 or 403)
                {
                    _metrics.RecordRequest(resourceTag, "error");
                    var body = await SafeReadBodyAsync(response, ct).ConfigureAwait(false);
                    response.Dispose();
                    response = null;
                    throw new DiscogsApiException(
                        $"Discogs authorization failed ({status}): {Truncate(body)}",
                        status);
                }

                if (status == 429)
                {
                    var retryAfter = RetryAfterHeader.Read(response, _time.GetUtcNow())
                        ?? TimeSpan.FromSeconds(60);
                    response.Dispose();
                    response = null;
                    _gate.Release();

                    if (attempt >= maxRetries || retryAfter.TotalSeconds > opts.MaxRetryWaitSeconds)
                    {
                        _metrics.RecordRequest(resourceTag, "rate_limited");
                        throw new ProviderRateLimitedException(retryAfter);
                    }

                    _metrics.RecordRetry();
                    _logger.LogWarning(
                        "Discogs rate limited for {Resource}; retrying after {Delay}s (attempt {Attempt}/{Max})",
                        resourceTag, retryAfter.TotalSeconds, attempt + 1, maxRetries);
                    await Task.Delay(retryAfter, _time, ct).ConfigureAwait(false);
                    continue;
                }

                if (status >= 500)
                {
                    response.Dispose();
                    response = null;
                    _gate.Release();

                    if (attempt >= maxRetries)
                    {
                        _metrics.RecordRequest(resourceTag, "error");
                        throw new DiscogsApiException($"Discogs server error ({status})", status);
                    }

                    _metrics.RecordRetry();
                    var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    _logger.LogWarning(
                        "Discogs HTTP {Status} for {Resource}; retrying after {Delay}s (attempt {Attempt}/{Max})",
                        status, resourceTag, backoff.TotalSeconds, attempt + 1, maxRetries);
                    await Task.Delay(backoff, _time, ct).ConfigureAwait(false);
                    continue;
                }

                // Success, 404, or other non-retryable status — hand the response to the caller.
                // Keep the concurrency permit released before returning so the caller doesn't hold it
                // while reading the body.
                _gate.Release();
                return response;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                response?.Dispose();
                try { _gate.Release(); } catch (SemaphoreFullException) { /* already released */ }
                throw;
            }
            catch (ProviderRateLimitedException)
            {
                throw;
            }
            catch (DiscogsApiException)
            {
                try { _gate.Release(); } catch (SemaphoreFullException) { /* already released */ }
                throw;
            }
            catch
            {
                response?.Dispose();
                try { _gate.Release(); } catch (SemaphoreFullException) { /* already released */ }
                throw;
            }
        }
    }

    private void EnsureSuccessOrThrow(HttpResponseMessage response, string resourceTag)
    {
        if (response.IsSuccessStatusCode)
            return;

        var status = (int)response.StatusCode;
        _metrics.RecordRequest(resourceTag, "error");
        throw new DiscogsApiException($"Discogs request failed with {status}", status);
    }

    private void NoteRateLimitHeaders(HttpResponseMessage response)
    {
        var limit = ReadIntHeader(response, "X-Discogs-Ratelimit");
        var used = ReadIntHeader(response, "X-Discogs-Ratelimit-Used");
        var remaining = ReadIntHeader(response, "X-Discogs-Ratelimit-Remaining");
        var observed = _time.GetUtcNow().UtcDateTime;
        LastRateLimit = new DiscogsRateLimitSnapshot(limit, used, remaining, observed);
        _gate.NoteRemaining(remaining, _time.GetUtcNow());
    }

    private void ApplyHeaders(HttpRequestMessage request, DiscogsOptions opts)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", opts.UserAgent);
        if (opts.HasToken)
            request.Headers.TryAddWithoutValidation("Authorization", $"Discogs token={opts.Token.Trim()}");
    }

    private Uri BuildUri(string relativePath)
    {
        var baseUrl = _options.CurrentValue.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = "https://api.discogs.com/";
        if (!baseUrl.EndsWith('/'))
            baseUrl += "/";
        // Concatenate then parse so EscapeDataString encodings in the query survive Uri composition.
        return new Uri(baseUrl + relativePath.TrimStart('/'), UriKind.Absolute);
    }

    private static DiscogsPage<T> ParsePage<T>(string json, string itemsProperty)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var pagination = root.TryGetProperty("pagination", out var pagEl)
            ? pagEl.Deserialize<DiscogsPagination>(DiscogsJson.Options) ?? new DiscogsPagination(1, 0, 0, 0)
            : new DiscogsPagination(1, 0, 0, 0);
        IReadOnlyList<T> items = [];
        if (root.TryGetProperty(itemsProperty, out var itemsEl))
            items = itemsEl.Deserialize<List<T>>(DiscogsJson.Options) ?? [];
        return new DiscogsPage<T>(pagination, items);
    }

    private static (int page, int perPage) ClampPaging(int page, int perPage) =>
        (Math.Max(1, page), Math.Clamp(perPage, 1, 100));

    private static DiscogsPage<DiscogsSearchResult> EmptySearchPage(int perPage) =>
        new(new DiscogsPagination(1, 0, perPage, 0), []);

    private static void AppendQuery(StringBuilder qs, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        qs.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
    }

    private static int? ReadIntHeader(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values))
            return null;
        var raw = values.FirstOrDefault();
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Truncate(string body) =>
        body.Length <= 200 ? body : body[..200];
}
