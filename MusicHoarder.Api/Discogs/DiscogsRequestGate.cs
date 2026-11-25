namespace MusicHoarder.Api.Discogs;

/// <summary>
/// Per-instance Discogs throttle: at most N requests in flight, starts spaced to the configured
/// requests-per-minute, and a hard hold when the last response said Remaining ≤ 1.
/// </summary>
public sealed class DiscogsRequestGate
{
    private readonly TimeProvider _time;
    private readonly object _schedule = new();
    private SemaphoreSlim? _concurrency;
    private DateTimeOffset _nextStartUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _holdUntilUtc = DateTimeOffset.MinValue;

    public DiscogsRequestGate(TimeProvider time) => _time = time;

    /// <summary>
    /// Claims the next start slot (spacing + Remaining-hold) then takes a concurrency permit.
    /// Call <see cref="Release"/> when the HTTP attempt finishes (success or failure).
    /// </summary>
    public async Task AcquireAsync(int maxConcurrency, int requestsPerMinute, CancellationToken ct)
    {
        var sem = SemaphoreFor(Math.Max(1, maxConcurrency));

        TimeSpan delay;
        lock (_schedule)
        {
            var now = _time.GetUtcNow();
            var earliest = now;
            if (_holdUntilUtc > earliest) earliest = _holdUntilUtc;
            if (_nextStartUtc > earliest) earliest = _nextStartUtc;

            delay = earliest > now ? earliest - now : TimeSpan.Zero;
            var spacing = TimeSpan.FromSeconds(60.0 / Math.Max(1, requestsPerMinute));
            _nextStartUtc = earliest + spacing;
        }

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, _time, ct).ConfigureAwait(false);

        await sem.WaitAsync(ct).ConfigureAwait(false);
    }

    public void Release()
    {
        var sem = _concurrency;
        sem?.Release();
    }

    /// <summary>
    /// When Discogs reports Remaining ≤ 1, the next request must wait until 60 s after that response.
    /// </summary>
    public void NoteRemaining(int? remaining, DateTimeOffset observedAtUtc)
    {
        if (remaining is null or > 1)
            return;

        var until = observedAtUtc + TimeSpan.FromSeconds(60);
        lock (_schedule)
        {
            if (until > _holdUntilUtc)
                _holdUntilUtc = until;
        }
    }

    private SemaphoreSlim SemaphoreFor(int maxConcurrency)
    {
        lock (_schedule)
        {
            if (_concurrency is not null)
                return _concurrency;

            _concurrency = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            return _concurrency;
        }
    }
}
