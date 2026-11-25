using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Catalog.Ingestion;

/// <summary>Shared fixtures for catalog ingestion tests (own copy — do not share with DiscogsContractTests).</summary>
internal static class CatalogIngestionTestKit
{
    public static readonly DateTimeOffset Start = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    public static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    public static string FixtureJson(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Discogs", name));

    public static DiscogsDocument<T> Document<T>(string fixtureName)
    {
        var raw = FixtureJson(fixtureName);
        var value = JsonSerializer.Deserialize<T>(raw, DiscogsJson.Options)!;
        return new DiscogsDocument<T>(value, raw);
    }

    public static DiscogsOptions Options(Action<DiscogsOptions>? configure = null)
    {
        var opts = new DiscogsOptions
        {
            Enabled = true,
            RefreshDays = 30,
            DownloadImages = false,
            ImageDirectory = Path.Combine(Path.GetTempPath(), "mh-catalog-images-" + Guid.NewGuid().ToString("N")),
        };
        configure?.Invoke(opts);
        return opts;
    }

    public static IOptionsMonitor<DiscogsOptions> Monitor(DiscogsOptions opts) =>
        new StaticOptionsMonitor<DiscogsOptions>(opts);

    public static FixedTimeProvider Clock(DateTimeOffset? now = null) => new(now ?? Start);

    public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
