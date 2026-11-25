using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Enrichment;
using Microsoft.Extensions.Caching.Memory;

namespace MusicHoarder.Api.Catalog;

internal sealed record ProfileLink(string Name, string Url);
internal sealed record LibraryProfile(
    string Name, string? Profile, string? Country, string? RealName, string? Image,
    ProfileLink? Parent, IReadOnlyList<ProfileLink> Aliases, IReadOnlyList<ProfileLink> Groups,
    IReadOnlyList<string> Variations, IReadOnlyList<string> Links, string? DiscogsUrl);

internal static partial class LibraryProfiles
{
    // Older installations have MusicBrainz country/area and verified Discogs IDs outside the EF catalog.
    private static async Task<JsonElement?> StoredLabelAsync(MusicHoarderDbContext db, string name, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql() || !await db.Database.SqlQueryRaw<bool>(
            """SELECT to_regclass('mh_label_profiles') IS NOT NULL AS "Value" """).SingleAsync(ct))
            return null;

        var json = await db.Database.SqlQuery<string>(
            $"""SELECT metadata::text AS "Value" FROM mh_label_profiles WHERE name_key = lower({name})""").FirstOrDefaultAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static string? Field(JsonElement? row, string key) =>
        row is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(key, out var value)
            && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : null;

    private static string? Web(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var u)
        && u.Scheme is "http" or "https" && u.UserInfo.Length == 0 ? u.AbsoluteUri : null;

    private static readonly Dictionary<string, string> CountryCodes = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
        .Select(c => new RegionInfo(c.Name)).DistinctBy(region => region.EnglishName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(region => region.EnglishName, region => region.TwoLetterISORegionName, StringComparer.OrdinalIgnoreCase);

    // Accept an explicit location clause for the label. Touring, distribution and release markets aren't an origin.
    [GeneratedRegex(@"(?:^|[.!?]\s+|[\r\n]+)\s*(?:(?:[\p{L}-]+\s+){0,6}label(?:\s+[^.!?\r\n]{1,160}?\s+and)?\s+)?based in\s+([\p{L} '-]+)(?:,\s*(?:in the [\p{L}-]+ part of\s+)?([\p{L} '-]+))?\s*(?=[.!?\r\n]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BasedIn();

    internal static string? CountryFromProfile(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile)) return null;
        var countries = BasedIn().Matches(profile).Select(m => (m.Groups[2].Success ? m.Groups[2] : m.Groups[1]).Value.Trim())
            .Select(place => CountryCodes.GetValueOrDefault(place)).OfType<string>().Distinct().ToArray();
        return countries.Length == 1 ? countries[0] : null;
    }

    private static string? Portrait(IReadOnlyList<DiscogsImage>? images) => images?
        .OrderByDescending(i => i.Type == "primary").Select(i => Web(i.Uri) ?? Web(i.Uri150)).FirstOrDefault(i => i != null);

    private static ProfileLink[] Artists(IReadOnlyList<DiscogsArtistRef>? refs) =>
        (refs ?? []).Where(a => a.Id > 0 && !string.IsNullOrWhiteSpace(a.Name)).DistinctBy(a => a.Id)
            .Select(a => new ProfileLink(a.Name!, $"https://www.discogs.com/artist/{a.Id}")).ToArray();

    internal static long? UniqueExactId(DiscogsPage<DiscogsSearchResult> page, string kind, string name)
    {
        // A truncated result set or numbered homonym cannot establish identity.
        if (page.Pagination.Pages > 1) return null;
        var ids = page.Items.Where(r => r.Type == kind && r.Title != null &&
            CatalogKeys.NameKey(r.Title) == CatalogKeys.NameKey(name)).ToList();
        return ids.Count == 1 && string.Equals(ids[0].Title, name, StringComparison.OrdinalIgnoreCase)
            ? ids[0].Id : null;
    }

    internal static async Task<IResult> GetAsync(string kind, string name, MusicHoarderDbContext db,
        ICatalogQueryService catalog, IDiscogsClient discogs, IMusicBrainzWebService musicBrainz,
        IMemoryCache cache, CancellationToken ct)
    {
        name = name.Trim();
        if (kind is not ("artist" or "label") || name.Length is 0 or > 512) return Results.BadRequest();
        var stored = kind == "label" ? await StoredLabelAsync(db, name, ct) : null;
        var fallback = new LibraryProfile(name, Field(stored, "description"), Field(stored, "country"),
            null, Web(Field(stored, "image")), null, [], [], [], [], Web(Field(stored, "discogsUrl")));
        long? id = long.TryParse(Field(stored, "discogsId"), out var labelId) ? labelId : null;
        if (kind == "artist")
        {
            var localId = await catalog.FindArtistForLibraryNameAsync(name, ct);
            if (localId.HasValue)
                id = await db.CatalogArtists.Where(a => a.Id == localId).Select(a => (long?)a.DiscogsId).SingleOrDefaultAsync(ct);
        }
        try
        {
            if (id == null)
            {
                var result = await discogs.SearchAsync(new(Type: kind, Query: name), perPage: 100, ct: ct);
                id = UniqueExactId(result, kind, name);
            }
            if (id == null) return stored is null ? Results.NoContent() : Results.Ok(fallback);
            var snapshot = await db.CatalogRawSnapshots.AsNoTracking()
                .Where(s => s.ResourceKind == kind && s.DiscogsId == id)
                .OrderByDescending(s => s.FetchedAtUtc).Select(s => s.PayloadJson).FirstOrDefaultAsync(ct);
            if (kind == "label")
            {
                var label = snapshot == null ? (await discogs.GetLabelAsync(id.Value, ct))?.Value :
                    JsonSerializer.Deserialize<DiscogsLabel>(snapshot, DiscogsJson.Options);
                if (label == null) return Results.Ok(fallback);
                var country = fallback.Country;
                if (string.IsNullOrWhiteSpace(country))
                {
                    try
                    {
                        country = await cache.GetOrCreateAsync($"discogs-label-country-{id}", async entry =>
                        {
                            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(12);
                            return await musicBrainz.CountryForDiscogsLabelAsync(id.Value, ct);
                        });
                    }
                    catch (ProviderRateLimitedException) { }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                }
                return Results.Ok(fallback with {
                    Country = country ?? CountryFromProfile(label.Profile),
                    Profile = label.Profile ?? fallback.Profile,
                    Image = Portrait(label.Images) ?? fallback.Image,
                    Parent = label.ParentLabel is { Id: > 0, Name: {} parent } p
                        ? new(parent, $"https://www.discogs.com/label/{p.Id}") : null,
                    Links = (label.Urls ?? []).Select(Web).OfType<string>().Distinct().ToArray(),
                    DiscogsUrl = $"https://www.discogs.com/label/{id}"
                });
            }
            var artist = snapshot == null ? (await discogs.GetArtistAsync(id.Value, ct))?.Value :
                JsonSerializer.Deserialize<DiscogsArtist>(snapshot, DiscogsJson.Options);
            return artist == null ? Results.NoContent() : Results.Ok(fallback with {
                Profile = artist.Profile, Image = Portrait(artist.Images), RealName = artist.Realname,
                Aliases = Artists(artist.Aliases), Groups = Artists(artist.Groups), Variations = artist.Namevariations ?? [],
                Links = (artist.Urls ?? []).Select(Web).OfType<string>().Distinct().ToArray(),
                DiscogsUrl = $"https://www.discogs.com/artist/{id}"
            });
        }
        catch (Exception ex) when (ex is DiscogsApiException or ProviderRateLimitedException)
        {
            return stored != null ? Results.Ok(fallback) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
