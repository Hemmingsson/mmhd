using System.Text.Json;
using MusicHoarder.Api.Catalog.Acquisition;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Catalog.Query;

/// <summary>Shared mapping helpers for catalog wire DTOs. Image API path: <c>/api/catalog/images/{id}</c>.</summary>
internal static class CatalogDtoMapper
{
    public const string LocalImagePathPrefix = "/api/catalog/images/";

    public static string FetchStateName(CatalogFetchState state) => state.ToString();

    public static CatalogImageDto MapImage(CatalogImage image)
    {
        var local = image.Status == CatalogImageStatus.Downloaded && !string.IsNullOrWhiteSpace(image.LocalPath);
        return new CatalogImageDto(
            image.Id,
            image.Type,
            local ? LocalImagePathPrefix + image.Id : NullIfEmpty(image.SourceUrl),
            NullIfEmpty(image.ThumbnailUrl),
            image.Width > 0 ? image.Width : null,
            image.Height > 0 ? image.Height : null,
            local);
    }

    public static CatalogUrlDto MapUrl(CatalogUrl url) =>
        new(url.Url, CatalogPurchaseLinks.Classify(url.Url));

    public static CatalogExternalIdDto MapExternal(CatalogExternalId id) =>
        new(id.Provider, id.ExternalId, id.Source);

    public static CatalogRefDto MapArtistRef(long discogsId, string name, int? catalogId, CatalogFetchState? state = null) =>
        new(catalogId, discogsId, name, state is null ? CatalogFetchState.Listed.ToString() : state.Value.ToString());

    public static CatalogRefDto MapLabelRef(long discogsId, string name, int? catalogId, CatalogFetchState? state = null) =>
        new(catalogId, discogsId, name, state is null ? CatalogFetchState.Listed.ToString() : state.Value.ToString());

    public static CatalogCreditDto MapCredit(CatalogCredit credit, IReadOnlyDictionary<long, CatalogArtist> artistsByDiscogsId)
    {
        artistsByDiscogsId.TryGetValue(credit.ArtistDiscogsId, out var artist);
        return new CatalogCreditDto(
            MapArtistRef(credit.ArtistDiscogsId, credit.Name, artist?.Id, artist?.FetchState),
            NullIfEmpty(credit.Anv),
            NullIfEmpty(credit.Join),
            NullIfEmpty(credit.Role),
            NullIfEmpty(credit.Tracks));
    }

    public static CatalogReleaseLabelDto MapReleaseLabel(
        CatalogReleaseLabel label, IReadOnlyDictionary<long, CatalogLabel> labelsByDiscogsId)
    {
        labelsByDiscogsId.TryGetValue(label.LabelDiscogsId, out var catalogLabel);
        return new CatalogReleaseLabelDto(
            MapLabelRef(label.LabelDiscogsId, label.Name, catalogLabel?.Id, catalogLabel?.FetchState),
            NullIfEmpty(label.CatalogNumber),
            NullIfEmpty(label.EntityTypeName));
    }

    public static CatalogFormatDto MapFormat(CatalogReleaseFormat format) =>
        new(format.Name, NullIfEmpty(format.Quantity), NullIfEmpty(format.Text), ParseDescriptions(format.DescriptionsJson));

    public static CatalogIdentifierDto MapIdentifier(CatalogReleaseIdentifier id) =>
        new(id.Type, id.Value, NullIfEmpty(id.Description));

    public static IReadOnlyList<CatalogTrackDto> MapTrackTree(
        IReadOnlyList<CatalogTrack> tracks,
        IReadOnlyList<CatalogCredit> credits,
        IReadOnlyDictionary<long, CatalogArtist> artistsByDiscogsId,
        IReadOnlySet<string> ownedTitleKeys)
    {
        var byParent = tracks
            .Where(t => t.ParentSequence is not null)
            .GroupBy(t => t.ParentSequence!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Sequence).ToList());

        var creditsByTrack = credits
            .Where(c => c.TrackSequence is not null)
            .GroupBy(c => c.TrackSequence!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Sequence).ToList());

        CatalogTrackDto MapOne(CatalogTrack track)
        {
            creditsByTrack.TryGetValue(track.Sequence, out var trackCredits);
            byParent.TryGetValue(track.Sequence, out var subs);
            var owned = ownedTitleKeys.Contains(CatalogKeys.TitleKey(track.Title));
            return new CatalogTrackDto(
                track.Sequence,
                NullIfEmpty(track.Position),
                track.Type,
                track.Title,
                NullIfEmpty(track.Duration),
                track.DurationSeconds,
                track.DiscNumber,
                track.TrackNumber,
                (trackCredits ?? []).Select(c => MapCredit(c, artistsByDiscogsId)).ToList(),
                (subs ?? []).Select(MapOne).ToList(),
                owned);
        }

        return tracks
            .Where(t => t.ParentSequence is null)
            .OrderBy(t => t.Sequence)
            .Select(MapOne)
            .ToList();
    }

    public static CatalogReleaseCardDto MapReleaseCard(
        CatalogRelease release, IReadOnlyList<string> styles, bool owned, string? thumbnailUrl) =>
        new(
            release.Id,
            release.DiscogsId,
            release.Title,
            release.ArtistDisplay,
            release.Year,
            release.Country,
            release.FormatSummary,
            release.LabelSummary,
            release.CatalogNumberSummary,
            FetchStateName(release.FetchState),
            thumbnailUrl,
            styles,
            release.MasterId,
            owned);

    public static CatalogMasterCardDto MapMasterCard(
        CatalogMaster master, IReadOnlyList<string> styles, bool owned, string? thumbnailUrl, int versionCount) =>
        new(
            master.Id,
            master.DiscogsId,
            master.Title,
            master.ArtistDisplay,
            master.Year,
            FetchStateName(master.FetchState),
            thumbnailUrl,
            styles,
            versionCount,
            owned);

    public static CatalogDiscographyItemDto MapDiscographyItem(
        CatalogDiscographyEntry entry,
        CatalogMaster? master,
        CatalogRelease? release,
        bool owned,
        string? thumbnailUrl)
    {
        var kind = entry.ItemKind == CatalogEntityKind.Master ? "master" : "release";
        var title = release?.Title ?? master?.Title ?? "";
        var artist = release?.ArtistDisplay ?? master?.ArtistDisplay;
        var year = release?.Year ?? master?.Year;
        return new CatalogDiscographyItemDto(
            kind,
            entry.MasterId ?? master?.Id,
            entry.ReleaseId ?? release?.Id,
            entry.ItemDiscogsId,
            title,
            artist,
            year,
            NullIfEmpty(entry.Role),
            release?.FormatSummary,
            release?.LabelSummary,
            NullIfEmpty(entry.CatalogNumber) ?? release?.CatalogNumberSummary,
            thumbnailUrl,
            owned);
    }

    public static IReadOnlyList<string> ParseDescriptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
