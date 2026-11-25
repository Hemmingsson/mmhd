using System.Text.Json;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Matching;

namespace MusicHoarder.Api.Metadata;

/// <summary>Original provider vocabulary, independent of the canonical Discogs genre taxonomy.</summary>
internal static class ProviderTags
{
    public static string[] Combine(IEnumerable<string?> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .SelectMany(value => value!.Split([';', '|', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string[] LegacyTerms(string? genre) => Combine([genre]).Where(term =>
        !DiscogsGenres.Tags(term).Contains(term, StringComparer.OrdinalIgnoreCase)).ToArray();

    public static string[] FromResult(string? genre, string[]? tags, string? provider) =>
        string.Equals(provider, nameof(EnrichmentProvider.Discogs), StringComparison.OrdinalIgnoreCase)
            ? [] : tags is not null ? Combine(tags) : Combine([genre]);

    /// <summary>Restores legacy evidence only when its identifiers or exact names describe this recording.</summary>
    public static (string[] Tags, string? Genre) Recover(
        string? genre, string[] tags, string? artist, string? title, string? isrc, string? musicBrainzId,
        string? spotifyId, IEnumerable<SongProviderAttempt> attempts)
    {
        var terms = new List<string?>(tags);
        // A previous cutover may already have canonicalized Genre. Such values do not prove a
        // provider supplied that spelling; only preserve still-original legacy vocabulary here.
        terms.AddRange(LegacyTerms(genre));
        var canonical = new List<string?> { genre };
        foreach (var attempt in attempts.Where(a => a.Status == ProviderAttemptStatus.Matched && a.MatchedDataJson is not null))
        {
            try
            {
                var result = JsonSerializer.Deserialize<EnrichmentProviderResult>(attempt.MatchedDataJson!);
                if (result is null) continue;
                bool SameId(string? left, string? right) => !string.IsNullOrWhiteSpace(left)
                    && !string.IsNullOrWhiteSpace(right) && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
                var sameNames = !string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title)
                    && TitleNormalizer.NormalizeForSearch(artist) == TitleNormalizer.NormalizeForSearch(result.Artist)
                    && TitleNormalizer.NormalizeForSearch(title) == TitleNormalizer.NormalizeForSearch(result.Title);
                if (!sameNames && !SameId(ProviderIdentity.NormalizeIsrc(isrc), ProviderIdentity.NormalizeIsrc(result.Isrc))
                    && !SameId(musicBrainzId, result.MusicBrainzId) && !SameId(spotifyId, result.SpotifyId)) continue;
                if (attempt.Provider != EnrichmentProvider.Discogs)
                    terms.AddRange(FromResult(result.Genre, result.Tags, result.MatchedBy));
                canonical.Add(result.Genre);
            }
            catch (JsonException) { /* Keep recoverable evidence when another legacy payload is corrupt. */ }
        }
        var recovered = Combine(terms);
        return (recovered, DiscogsGenres.Normalize(MultiValue.Join(canonical.Concat(recovered))));
    }

    public static EnrichmentProviderResult WithEvidence(
        EnrichmentProviderResult winner, IEnumerable<SongProviderAttempt> attempts,
        IReadOnlyCollection<EnrichmentProvider> agreeingProviders)
    {
        var evidence = new List<EnrichmentProviderResult> { winner };
        foreach (var attempt in attempts.Where(a => agreeingProviders.Contains(a.Provider) && a.Status == ProviderAttemptStatus.Matched && a.MatchedDataJson is not null))
        {
            try
            {
                if (JsonSerializer.Deserialize<EnrichmentProviderResult>(attempt.MatchedDataJson!) is { } result)
                    evidence.Add(attempt.Provider == EnrichmentProvider.Discogs ? result with { Tags = [], MatchedBy = nameof(EnrichmentProvider.Discogs) } : result);
            }
            catch (JsonException) { /* A legacy corrupt payload cannot discard the other providers. */ }
        }
        var tags = Combine(evidence.SelectMany(result => FromResult(result.Genre, result.Tags, result.MatchedBy)));
        return winner with { Tags = tags, Genre = DiscogsGenres.Normalize(MultiValue.Join(evidence.Select(result => result.Genre).Concat(tags))) };
    }
}
