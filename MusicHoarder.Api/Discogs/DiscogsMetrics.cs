using System.Diagnostics.Metrics;

namespace MusicHoarder.Api.Discogs;

/// <summary>OpenTelemetry instruments for Discogs HTTP traffic. Low-cardinality tags only.</summary>
public sealed class DiscogsMetrics
{
    public const string MeterName = "MusicHoarder.Discogs";

    private readonly Counter<long> _requests;
    private readonly Counter<long> _retries;
    private Func<int?> _remainingProvider = static () => null;

    public DiscogsMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _requests = meter.CreateCounter<long>(
            "musichoarder.discogs.requests",
            unit: "{requests}",
            description: "Discogs HTTP outcomes, tagged by resource and outcome.");

        _retries = meter.CreateCounter<long>(
            "musichoarder.discogs.retries",
            unit: "{retries}",
            description: "Retries after 429 or transient failures.");

        meter.CreateObservableGauge(
            "musichoarder.discogs.ratelimit.remaining",
            () => _remainingProvider() ?? -1,
            unit: "{requests}",
            description: "Last X-Discogs-Ratelimit-Remaining seen (-1 when unknown).");
    }

    /// <summary>Wire the remaining gauge to the client's last rate-limit snapshot.</summary>
    public Func<int?> RemainingProvider
    {
        get => _remainingProvider;
        set => _remainingProvider = value ?? (static () => null);
    }

    public void RecordRequest(string resource, string outcome) =>
        _requests.Add(1,
            new KeyValuePair<string, object?>("resource", resource),
            new KeyValuePair<string, object?>("outcome", outcome));

    public void RecordRetry() => _retries.Add(1);
}
