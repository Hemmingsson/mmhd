using System.Text.Json;
using System.Text.RegularExpressions;

namespace MusicHoarder.Api.Metadata;

/// <summary>Canonicalizes legacy and provider-supplied tags against the library browser’s Discogs vocabulary.</summary>
internal static partial class DiscogsGenres
{
    private sealed record Taxonomy(string[] Genres, string[] Styles, Dictionary<string, string> Aliases);

    private static readonly Taxonomy Data = LoadTaxonomy();
    private static readonly HashSet<string> BroadGenres = new(Data.Genres, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Vocabulary = BuildVocabulary();
    private static readonly int MaxParts = Vocabulary.Keys.Max(key => CompoundSeparator().Split(key).Length);

    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"\s*([/,])\s*")] private static partial Regex SeparatorSpacing();
    [GeneratedRegex(@"([/,])")] private static partial Regex CompoundSeparator();

    private static string Key(string value) =>
        SeparatorSpacing().Replace(Whitespace().Replace(value.Trim(), " "), "$1");

    private static Taxonomy LoadTaxonomy()
    {
        using var stream = typeof(DiscogsGenres).Assembly.GetManifestResourceStream("DiscogsTaxonomy.json")
            ?? throw new InvalidOperationException("Missing Discogs taxonomy.");
        return JsonSerializer.Deserialize<Taxonomy>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Invalid Discogs taxonomy.");
    }

    public static bool IsBroadGenre(string raw) => BroadGenres.Contains(raw.Trim());

    private static Dictionary<string, string> BuildVocabulary()
    {
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in Data.Genres.Concat(Data.Styles))
            dictionary[Key(term)] = term;
        foreach (var (alias, canonical) in Data.Aliases)
            dictionary[Key(alias)] = dictionary[Key(canonical)];
        return dictionary;
    }

    public static string? Normalize(string? raw) => MultiValue.Join(Tags(raw));

    public static string[] Tags(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var result = new List<string>();
        foreach (var field in raw.Split([';', '|', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            // Keep the longest complete term, including punctuation within "Folk, World, & Country".
            var parts = CompoundSeparator().Split(field);
            for (var start = 0; start < parts.Length; start += 2)
            {
                for (var end = Math.Min(start + MaxParts, parts.Length); end > start; end -= 2)
                {
                    if (!Vocabulary.TryGetValue(Key(string.Join("", parts, start, end - start)), out var term))
                        continue;
                    if (!result.Contains(term)) result.Add(term);
                    start = end - 1;
                    break;
                }
            }
        }
        return result.ToArray();
    }
}
