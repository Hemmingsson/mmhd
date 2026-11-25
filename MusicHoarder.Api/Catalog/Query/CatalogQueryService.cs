using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Catalog.Acquisition;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Catalog.Query;

/// <summary>
/// Read side of the Discogs catalog. Ownership uses the ambient SongMetadata owner filter.
/// Complexity notes (queries per call) are summarized on each public method.
/// </summary>
public sealed class CatalogQueryService(MusicHoarderDbContext db) : ICatalogQueryService
{
    private const int DiscographyCap = 500;
    private const int VersionsCap = 200;
    private const int FacetTop = 50;

    /// <summary>~2–3 queries: count + page + owned-release counts for page.</summary>
    public async Task<CatalogPage<CatalogArtistCardDto>> ListArtistsAsync(CatalogListQuery query, CancellationToken ct = default)
    {
        var q = db.CatalogArtists.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var key = CatalogKeys.NameKey(query.Q);
            q = q.Where(a => a.NameKey.Contains(key) || a.Name.ToLower().Contains(query.Q.Trim().ToLower()));
        }

        var total = await q.CountAsync(ct);
        var page = await q
            .OrderBy(a => a.NameKey).ThenBy(a => a.Id)
            .Skip((query.SafePage - 1) * query.SafePageSize)
            .Take(query.SafePageSize)
            .ToListAsync(ct);

        var artistIds = page.Select(a => a.Id).ToList();
        var discogsIds = page.Select(a => a.DiscogsId).ToList();

        var releaseCounts = await db.CatalogDiscographyEntries.AsNoTracking()
            .Where(e => e.OwnerKind == CatalogDiscographyOwner.Artist && artistIds.Contains(e.OwnerId))
            .GroupBy(e => e.OwnerId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var ownedByArtist = await db.Songs.AsNoTracking()
            .Where(s => s.DeletedAtUtc == null && s.DiscogsArtistId != null && discogsIds.Contains(s.DiscogsArtistId.Value))
            .GroupBy(s => s.DiscogsArtistId!.Value)
            .Select(g => new { g.Key, Releases = g.Select(s => s.DiscogsReleaseId).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Releases, ct);

        var thumbs = await PrimaryThumbnailsAsync(CatalogEntityKind.Artist, artistIds, ct);

        var items = page.Select(a => new CatalogArtistCardDto(
            a.Id, a.DiscogsId, a.Name, CatalogDtoMapper.FetchStateName(a.FetchState),
            thumbs.GetValueOrDefault(a.Id) ?? NullThumb(null),
            releaseCounts.GetValueOrDefault(a.Id),
            ownedByArtist.GetValueOrDefault(a.DiscogsId))).ToList();

        return new CatalogPage<CatalogArtistCardDto>(items, total, query.SafePage, query.SafePageSize);
    }

    /// <summary>~8–10 queries: artist + relations + urls/images/external + discography + styles + ownership + library names.</summary>
    public async Task<CatalogArtistDetailDto?> GetArtistAsync(int id, CancellationToken ct = default)
    {
        var artist = await db.CatalogArtists.AsNoTracking()
            .Include(a => a.NameVariations)
            .Include(a => a.Relations)
            .FirstOrDefaultAsync(a => a.Id == id, ct);
        if (artist is null)
            return null;

        var relatedDiscogsIds = artist.Relations.Select(r => r.RelatedDiscogsId).Distinct().ToList();
        var relatedArtists = await db.CatalogArtists.AsNoTracking()
            .Where(a => relatedDiscogsIds.Contains(a.DiscogsId))
            .ToDictionaryAsync(a => a.DiscogsId, ct);

        CatalogArtistRelationDto MapRel(CatalogArtistRelation r)
        {
            relatedArtists.TryGetValue(r.RelatedDiscogsId, out var linked);
            return new CatalogArtistRelationDto(
                r.Kind.ToString().ToLowerInvariant(),
                CatalogDtoMapper.MapArtistRef(r.RelatedDiscogsId, r.RelatedName, linked?.Id, linked?.FetchState),
                r.Active);
        }

        var aliases = artist.Relations.Where(r => r.Kind == CatalogArtistRelationKind.Alias).OrderBy(r => r.Sequence).Select(MapRel).ToList();
        var members = artist.Relations.Where(r => r.Kind == CatalogArtistRelationKind.Member).OrderBy(r => r.Sequence).Select(MapRel).ToList();
        var groups = artist.Relations.Where(r => r.Kind == CatalogArtistRelationKind.Group).OrderBy(r => r.Sequence).Select(MapRel).ToList();

        var urls = await LoadUrlsAsync(CatalogEntityKind.Artist, id, ct);
        var images = await LoadImagesAsync(CatalogEntityKind.Artist, id, ct);
        var externalIds = await LoadExternalIdsAsync(CatalogEntityKind.Artist, id, ct);

        var entries = await db.CatalogDiscographyEntries.AsNoTracking()
            .Include(e => e.Master)
            .Include(e => e.Release)
            .Where(e => e.OwnerKind == CatalogDiscographyOwner.Artist && e.OwnerId == id)
            .OrderBy(e => e.Master != null ? e.Master.Year : e.Release != null ? e.Release.Year : int.MaxValue)
            .ThenBy(e => e.Sequence)
            .Take(DiscographyCap)
            .ToListAsync(ct);

        var discographyTotal = await db.CatalogDiscographyEntries.AsNoTracking()
            .CountAsync(e => e.OwnerKind == CatalogDiscographyOwner.Artist && e.OwnerId == id, ct);

        var releaseDiscogsIds = entries
            .Select(e => e.Release?.DiscogsId ?? e.ItemDiscogsId)
            .Distinct()
            .ToList();
        var masterDiscogsIds = entries
            .Where(e => e.ItemKind == CatalogEntityKind.Master)
            .Select(e => e.ItemDiscogsId)
            .Distinct()
            .ToList();

        var ownedReleaseIds = await OwnedReleaseDiscogsIdsAsync(releaseDiscogsIds, ct);
        var ownedMasterIds = await OwnedMasterDiscogsIdsAsync(masterDiscogsIds, ct);

        var releaseIds = entries.Where(e => e.ReleaseId is not null).Select(e => e.ReleaseId!.Value).Distinct().ToList();
        var masterIds = entries.Where(e => e.MasterId is not null).Select(e => e.MasterId!.Value).Distinct().ToList();
        var releaseThumbs = await PrimaryThumbnailsAsync(CatalogEntityKind.Release, releaseIds, ct);
        var masterThumbs = await PrimaryThumbnailsAsync(CatalogEntityKind.Master, masterIds, ct);

        var discography = entries.Select(e =>
        {
            var owned = e.ItemKind == CatalogEntityKind.Master
                ? ownedMasterIds.Contains(e.ItemDiscogsId)
                : ownedReleaseIds.Contains(e.Release?.DiscogsId ?? e.ItemDiscogsId);
            var thumb = e.ReleaseId is int rid
                ? releaseThumbs.GetValueOrDefault(rid) ?? e.Release?.Thumb
                : e.MasterId is int mid
                    ? masterThumbs.GetValueOrDefault(mid) ?? e.Master?.Thumb
                    : null;
            return CatalogDtoMapper.MapDiscographyItem(e, e.Master, e.Release, owned, thumb);
        }).ToList();

        var topStyles = await db.CatalogReleaseStyles.AsNoTracking()
            .Where(rs => db.CatalogCredits.Any(c =>
                c.ReleaseId == rs.ReleaseId && c.ArtistDiscogsId == artist.DiscogsId && c.Kind == CatalogCreditKind.Main))
            .GroupBy(rs => new { rs.Style.Name, rs.Style.NameKey })
            .Select(g => new { g.Key.Name, Count = g.Select(x => x.ReleaseId).Distinct().Count() })
            .OrderByDescending(x => x.Count).ThenBy(x => x.Name)
            .Take(20)
            .ToListAsync(ct);

        var ownedSongs = await db.Songs.AsNoTracking()
            .Where(s => s.DeletedAtUtc == null && s.DiscogsArtistId == artist.DiscogsId)
            .Select(s => new { s.DiscogsReleaseId, s.AlbumArtist, s.Artist })
            .ToListAsync(ct);

        var ownership = new CatalogOwnershipDto(
            ownedSongs.Count,
            ownedSongs.Select(s => s.DiscogsReleaseId).Where(id => id is not null).Distinct().Count());

        var libraryNames = ownedSongs
            .Select(s => string.IsNullOrWhiteSpace(s.AlbumArtist) ? s.Artist : s.AlbumArtist)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();

        return new CatalogArtistDetailDto(
            artist.Id,
            artist.DiscogsId,
            artist.Name,
            artist.RealName,
            artist.Profile,
            CatalogDtoMapper.FetchStateName(artist.FetchState),
            artist.DataQuality,
            artist.DiscogsUri,
            artist.FetchedAtUtc,
            artist.NameVariations.OrderBy(v => v.Sequence).Select(v => v.Name).ToList(),
            aliases, members, groups,
            urls.Select(CatalogDtoMapper.MapUrl).ToList(),
            images.Select(CatalogDtoMapper.MapImage).ToList(),
            externalIds.Select(CatalogDtoMapper.MapExternal).ToList(),
            topStyles.Select(s => new CatalogStyleDto(s.Name, s.Count)).ToList(),
            discography,
            discographyTotal,
            ownership,
            libraryNames);
    }

    /// <summary>~2–3 queries.</summary>
    public async Task<CatalogPage<CatalogLabelCardDto>> ListLabelsAsync(CatalogListQuery query, CancellationToken ct = default)
    {
        var q = db.CatalogLabels.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var key = CatalogKeys.NameKey(query.Q);
            q = q.Where(l => l.NameKey.Contains(key) || l.Name.ToLower().Contains(query.Q.Trim().ToLower()));
        }

        var total = await q.CountAsync(ct);
        var page = await q
            .OrderBy(l => l.NameKey).ThenBy(l => l.Id)
            .Skip((query.SafePage - 1) * query.SafePageSize)
            .Take(query.SafePageSize)
            .ToListAsync(ct);

        var ids = page.Select(l => l.Id).ToList();
        var releaseCounts = await db.CatalogDiscographyEntries.AsNoTracking()
            .Where(e => e.OwnerKind == CatalogDiscographyOwner.Label && ids.Contains(e.OwnerId))
            .GroupBy(e => e.OwnerId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var thumbs = await PrimaryThumbnailsAsync(CatalogEntityKind.Label, ids, ct);

        var items = page.Select(l => new CatalogLabelCardDto(
            l.Id, l.DiscogsId, l.Name, CatalogDtoMapper.FetchStateName(l.FetchState),
            thumbs.GetValueOrDefault(l.Id),
            releaseCounts.GetValueOrDefault(l.Id))).ToList();

        return new CatalogPage<CatalogLabelCardDto>(items, total, query.SafePage, query.SafePageSize);
    }

    /// <summary>~5–7 queries.</summary>
    public async Task<CatalogLabelDetailDto?> GetLabelAsync(int id, CancellationToken ct = default)
    {
        var label = await db.CatalogLabels.AsNoTracking()
            .Include(l => l.Sublabels)
            .FirstOrDefaultAsync(l => l.Id == id, ct);
        if (label is null)
            return null;

        CatalogRefDto? parent = null;
        if (label.ParentDiscogsId is long parentDiscogsId)
        {
            var parentRow = await db.CatalogLabels.AsNoTracking()
                .FirstOrDefaultAsync(l => l.DiscogsId == parentDiscogsId, ct);
            parent = CatalogDtoMapper.MapLabelRef(
                parentDiscogsId, label.ParentName ?? parentRow?.Name ?? "", parentRow?.Id, parentRow?.FetchState);
        }

        var subDiscogsIds = label.Sublabels.Select(s => s.DiscogsId).ToList();
        var subRows = await db.CatalogLabels.AsNoTracking()
            .Where(l => subDiscogsIds.Contains(l.DiscogsId))
            .ToDictionaryAsync(l => l.DiscogsId, ct);
        var sublabels = label.Sublabels.OrderBy(s => s.Sequence)
            .Select(s =>
            {
                subRows.TryGetValue(s.DiscogsId, out var row);
                return CatalogDtoMapper.MapLabelRef(s.DiscogsId, s.Name, row?.Id, row?.FetchState);
            }).ToList();

        var urls = await LoadUrlsAsync(CatalogEntityKind.Label, id, ct);
        var images = await LoadImagesAsync(CatalogEntityKind.Label, id, ct);

        var entries = await db.CatalogDiscographyEntries.AsNoTracking()
            .Include(e => e.Master)
            .Include(e => e.Release)
            .Where(e => e.OwnerKind == CatalogDiscographyOwner.Label && e.OwnerId == id)
            .OrderBy(e => e.Release != null ? e.Release.Year : e.Master != null ? e.Master.Year : int.MaxValue)
            .ThenBy(e => e.Sequence)
            .Take(DiscographyCap)
            .ToListAsync(ct);
        var releaseTotal = await db.CatalogDiscographyEntries.AsNoTracking()
            .CountAsync(e => e.OwnerKind == CatalogDiscographyOwner.Label && e.OwnerId == id, ct);

        var releaseDiscogsIds = entries.Select(e => e.Release?.DiscogsId ?? e.ItemDiscogsId).Distinct().ToList();
        var owned = await OwnedReleaseDiscogsIdsAsync(releaseDiscogsIds, ct);
        var releaseIds = entries.Where(e => e.ReleaseId is not null).Select(e => e.ReleaseId!.Value).Distinct().ToList();
        var thumbs = await PrimaryThumbnailsAsync(CatalogEntityKind.Release, releaseIds, ct);

        var releases = entries.Select(e => CatalogDtoMapper.MapDiscographyItem(
            e, e.Master, e.Release,
            owned.Contains(e.Release?.DiscogsId ?? e.ItemDiscogsId),
            e.ReleaseId is int rid ? thumbs.GetValueOrDefault(rid) ?? e.Release?.Thumb : e.Master?.Thumb)).ToList();

        return new CatalogLabelDetailDto(
            label.Id, label.DiscogsId, label.Name, label.Profile, label.ContactInfo,
            CatalogDtoMapper.FetchStateName(label.FetchState), label.DataQuality, label.DiscogsUri,
            parent, sublabels,
            urls.Select(CatalogDtoMapper.MapUrl).ToList(),
            images.Select(CatalogDtoMapper.MapImage).ToList(),
            releases, releaseTotal);
    }

    /// <summary>~3–4 queries.</summary>
    public async Task<CatalogPage<CatalogMasterCardDto>> ListMastersAsync(CatalogReleaseQuery query, CancellationToken ct = default)
    {
        var q = await FilterMastersAsync(db.CatalogMasters.AsNoTracking(), query, ct);
        var total = await q.CountAsync(ct);
        q = ApplyMasterSort(q, query.Sort);
        var page = await q
            .Skip((query.SafePage - 1) * query.SafePageSize)
            .Take(query.SafePageSize)
            .ToListAsync(ct);

        var ids = page.Select(m => m.Id).ToList();
        var discogsIds = page.Select(m => m.DiscogsId).ToList();
        var styles = await StylesForMastersAsync(ids, ct);
        var owned = await OwnedMasterDiscogsIdsAsync(discogsIds, ct);
        var thumbs = await PrimaryThumbnailsAsync(CatalogEntityKind.Master, ids, ct);
        var versionCounts = await db.CatalogReleases.AsNoTracking()
            .Where(r => r.MasterId != null && ids.Contains(r.MasterId.Value))
            .GroupBy(r => r.MasterId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var items = page.Select(m => CatalogDtoMapper.MapMasterCard(
            m,
            styles.GetValueOrDefault(m.Id) ?? [],
            owned.Contains(m.DiscogsId),
            thumbs.GetValueOrDefault(m.Id) ?? m.Thumb,
            versionCounts.GetValueOrDefault(m.Id, m.VersionCount))).ToList();

        return new CatalogPage<CatalogMasterCardDto>(items, total, query.SafePage, query.SafePageSize);
    }

    /// <summary>~6–8 queries.</summary>
    public async Task<CatalogMasterDetailDto?> GetMasterAsync(int id, CancellationToken ct = default)
    {
        var master = await db.CatalogMasters.AsNoTracking()
            .Include(m => m.Styles).ThenInclude(s => s.Style)
            .FirstOrDefaultAsync(m => m.Id == id, ct);
        if (master is null)
            return null;

        CatalogRelease? mainRelease = null;
        if (master.MainReleaseDiscogsId is long mainDiscogsId)
        {
            mainRelease = await db.CatalogReleases.AsNoTracking()
                .Include(r => r.Tracks)
                .Include(r => r.Credits)
                .FirstOrDefaultAsync(r => r.DiscogsId == mainDiscogsId, ct);
        }

        var versionsQuery = db.CatalogReleases.AsNoTracking().Where(r => r.MasterId == id);
        var versionTotal = await versionsQuery.CountAsync(ct);
        var versions = await versionsQuery
            .OrderByDescending(r => r.Year).ThenBy(r => r.Id)
            .Take(VersionsCap)
            .ToListAsync(ct);

        var versionIds = versions.Select(v => v.Id).ToList();
        var versionStyles = await StylesForReleasesAsync(versionIds, ct);
        var versionOwned = await OwnedReleaseDiscogsIdsAsync(versions.Select(v => v.DiscogsId).ToList(), ct);
        var versionThumbs = await PrimaryThumbnailsAsync(CatalogEntityKind.Release, versionIds, ct);

        var versionCards = versions.Select(v => CatalogDtoMapper.MapReleaseCard(
            v,
            versionStyles.GetValueOrDefault(v.Id) ?? [],
            versionOwned.Contains(v.DiscogsId),
            versionThumbs.GetValueOrDefault(v.Id) ?? v.Thumb)).ToList();

        var images = await LoadImagesAsync(CatalogEntityKind.Master, id, ct);
        var urls = await LoadUrlsAsync(CatalogEntityKind.Master, id, ct);

        IReadOnlyList<CatalogCreditDto> artists = [];
        IReadOnlyList<CatalogTrackDto> tracklist = [];
        if (mainRelease is not null)
        {
            var artistDiscogsIds = mainRelease.Credits.Select(c => c.ArtistDiscogsId).Distinct().ToList();
            var artistsById = await db.CatalogArtists.AsNoTracking()
                .Where(a => artistDiscogsIds.Contains(a.DiscogsId))
                .ToDictionaryAsync(a => a.DiscogsId, ct);
            artists = mainRelease.Credits
                .Where(c => c.Kind == CatalogCreditKind.Main && c.TrackSequence is null)
                .OrderBy(c => c.Sequence)
                .Select(c => CatalogDtoMapper.MapCredit(c, artistsById))
                .ToList();

            var ownedTitles = await OwnedTitleKeysForReleaseAsync(mainRelease.DiscogsId, ct);
            tracklist = CatalogDtoMapper.MapTrackTree(mainRelease.Tracks, mainRelease.Credits, artistsById, ownedTitles);
        }

        var ownedSongs = await db.Songs.AsNoTracking()
            .CountAsync(s => s.DeletedAtUtc == null && s.DiscogsMasterId == master.DiscogsId, ct);
        var ownedReleases = await db.Songs.AsNoTracking()
            .Where(s => s.DeletedAtUtc == null && s.DiscogsMasterId == master.DiscogsId && s.DiscogsReleaseId != null)
            .Select(s => s.DiscogsReleaseId)
            .Distinct()
            .CountAsync(ct);

        return new CatalogMasterDetailDto(
            master.Id, master.DiscogsId, master.Title, master.ArtistDisplay, master.Year, master.Notes,
            CatalogDtoMapper.FetchStateName(master.FetchState), master.DataQuality, master.DiscogsUri,
            master.MainReleaseDiscogsId, mainRelease?.Id,
            master.Styles.OrderBy(s => s.Sequence).Select(s => s.Style.Name).ToList(),
            artists,
            images.Select(CatalogDtoMapper.MapImage).ToList(),
            urls.Select(CatalogDtoMapper.MapUrl).ToList(),
            tracklist,
            versionCards,
            versionTotal,
            new CatalogOwnershipDto(ownedSongs, ownedReleases));
    }

    /// <summary>~3–4 queries.</summary>
    public async Task<CatalogPage<CatalogReleaseCardDto>> ListReleasesAsync(CatalogReleaseQuery query, CancellationToken ct = default)
    {
        var q = await FilterReleasesAsync(db.CatalogReleases.AsNoTracking(), query, ct);
        var total = await q.CountAsync(ct);
        q = ApplyReleaseSort(q, query.Sort);
        var page = await q
            .Skip((query.SafePage - 1) * query.SafePageSize)
            .Take(query.SafePageSize)
            .ToListAsync(ct);

        var ids = page.Select(r => r.Id).ToList();
        var discogsIds = page.Select(r => r.DiscogsId).ToList();
        var styles = await StylesForReleasesAsync(ids, ct);
        var owned = await OwnedReleaseDiscogsIdsAsync(discogsIds, ct);
        var thumbs = await PrimaryThumbnailsAsync(CatalogEntityKind.Release, ids, ct);

        var items = page.Select(r => CatalogDtoMapper.MapReleaseCard(
            r,
            styles.GetValueOrDefault(r.Id) ?? [],
            owned.Contains(r.DiscogsId),
            thumbs.GetValueOrDefault(r.Id) ?? r.Thumb)).ToList();

        return new CatalogPage<CatalogReleaseCardDto>(items, total, query.SafePage, query.SafePageSize);
    }

    /// <summary>~8–12 queries: release graph + related artists/labels + ownership + purchase urls.</summary>
    public async Task<CatalogReleaseDetailDto?> GetReleaseAsync(int id, CancellationToken ct = default)
    {
        var release = await db.CatalogReleases.AsNoTracking()
            .Include(r => r.Tracks)
            .Include(r => r.Credits)
            .Include(r => r.Labels)
            .Include(r => r.Formats)
            .Include(r => r.Identifiers)
            .Include(r => r.Styles).ThenInclude(s => s.Style)
            .Include(r => r.Master)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (release is null)
            return null;

        var artistDiscogsIds = release.Credits.Select(c => c.ArtistDiscogsId).Distinct().ToList();
        var artistsById = await db.CatalogArtists.AsNoTracking()
            .Where(a => artistDiscogsIds.Contains(a.DiscogsId))
            .ToDictionaryAsync(a => a.DiscogsId, ct);

        var labelDiscogsIds = release.Labels.Select(l => l.LabelDiscogsId).Distinct().ToList();
        var labelsById = await db.CatalogLabels.AsNoTracking()
            .Where(l => labelDiscogsIds.Contains(l.DiscogsId))
            .ToDictionaryAsync(l => l.DiscogsId, ct);

        var images = await LoadImagesAsync(CatalogEntityKind.Release, id, ct);
        var externalIds = await LoadExternalIdsAsync(CatalogEntityKind.Release, id, ct);

        var urlRows = await LoadUrlsAsync(CatalogEntityKind.Release, id, ct);
        var urlList = urlRows.Select(u => u.Url).ToList();
        if (release.MasterId is int mid)
        {
            urlList.AddRange((await LoadUrlsAsync(CatalogEntityKind.Master, mid, ct)).Select(u => u.Url));
        }
        foreach (var aid in release.Credits.Where(c => c.Kind == CatalogCreditKind.Main && c.TrackSequence is null)
                     .Select(c => c.ArtistDiscogsId).Distinct())
        {
            if (artistsById.TryGetValue(aid, out var a))
                urlList.AddRange((await LoadUrlsAsync(CatalogEntityKind.Artist, a.Id, ct)).Select(u => u.Url));
        }
        foreach (var lid in release.Labels.Where(l => l.Kind == CatalogReleaseLabelKind.Label).Select(l => l.LabelDiscogsId).Distinct())
        {
            if (labelsById.TryGetValue(lid, out var lab))
                urlList.AddRange((await LoadUrlsAsync(CatalogEntityKind.Label, lab.Id, ct)).Select(u => u.Url));
        }

        var purchaseUrls = CatalogPurchaseLinks.Build(release.DiscogsId, urlList);
        // Wire DTO uses CatalogUrlDto (Kind classified); purchase links are also represented as urls.
        var urls = purchaseUrls.Select(p => new CatalogUrlDto(p.Url, p.Kind)).ToList();

        var ownedTitles = await OwnedTitleKeysForReleaseAsync(release.DiscogsId, ct);
        var ownedSongs = await db.Songs.AsNoTracking()
            .CountAsync(s => s.DeletedAtUtc == null && s.DiscogsReleaseId == release.DiscogsId, ct);

        CatalogRefDto? masterRef = null;
        if (release.Master is not null || release.MasterDiscogsId is not null)
        {
            var discogsId = release.Master?.DiscogsId ?? release.MasterDiscogsId!.Value;
            var name = release.Master?.Title ?? "";
            masterRef = new CatalogRefDto(
                release.MasterId ?? release.Master?.Id,
                discogsId,
                name,
                CatalogDtoMapper.FetchStateName(release.Master?.FetchState ?? CatalogFetchState.Listed));
        }

        return new CatalogReleaseDetailDto(
            release.Id, release.DiscogsId, release.Title, release.ArtistDisplay, release.Year, release.Released,
            release.Country, release.Status, release.Notes,
            CatalogDtoMapper.FetchStateName(release.FetchState), release.DataQuality, release.DiscogsUri, release.FetchedAtUtc,
            masterRef,
            release.Styles.OrderBy(s => s.Sequence).Select(s => s.Style.Name).ToList(),
            release.Credits.Where(c => c.Kind == CatalogCreditKind.Main && c.TrackSequence is null)
                .OrderBy(c => c.Sequence).Select(c => CatalogDtoMapper.MapCredit(c, artistsById)).ToList(),
            release.Credits.Where(c => c.Kind == CatalogCreditKind.Extra && c.TrackSequence is null)
                .OrderBy(c => c.Sequence).Select(c => CatalogDtoMapper.MapCredit(c, artistsById)).ToList(),
            release.Labels.Where(l => l.Kind == CatalogReleaseLabelKind.Label).OrderBy(l => l.Sequence)
                .Select(l => CatalogDtoMapper.MapReleaseLabel(l, labelsById)).ToList(),
            release.Labels.Where(l => l.Kind == CatalogReleaseLabelKind.Company).OrderBy(l => l.Sequence)
                .Select(l => CatalogDtoMapper.MapReleaseLabel(l, labelsById)).ToList(),
            release.Labels.Where(l => l.Kind == CatalogReleaseLabelKind.Series).OrderBy(l => l.Sequence)
                .Select(l => CatalogDtoMapper.MapReleaseLabel(l, labelsById)).ToList(),
            release.Formats.OrderBy(f => f.Sequence).Select(CatalogDtoMapper.MapFormat).ToList(),
            release.Identifiers.OrderBy(i => i.Sequence).Select(CatalogDtoMapper.MapIdentifier).ToList(),
            CatalogDtoMapper.MapTrackTree(release.Tracks, release.Credits, artistsById, ownedTitles),
            images.Select(CatalogDtoMapper.MapImage).ToList(),
            urls,
            externalIds.Select(CatalogDtoMapper.MapExternal).ToList(),
            new CatalogOwnershipDto(ownedSongs, ownedSongs > 0 ? 1 : 0));
    }

    /// <summary>~2–3 queries.</summary>
    public async Task<CatalogPage<CatalogTrackRowDto>> ListTracksAsync(CatalogListQuery query, CancellationToken ct = default)
    {
        var q = db.CatalogTracks.AsNoTracking()
            .Where(t => t.ParentSequence == null && (t.Type == "track" || t.Type == "index" || t.Type == null));
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var key = CatalogKeys.TitleKey(query.Q);
            q = q.Where(t => t.TitleKey.Contains(key) || t.Title.ToLower().Contains(query.Q.Trim().ToLower()));
        }

        var total = await q.CountAsync(ct);
        var page = await q
            .OrderBy(t => t.TitleKey).ThenBy(t => t.Id)
            .Skip((query.SafePage - 1) * query.SafePageSize)
            .Take(query.SafePageSize)
            .Select(t => new
            {
                t.ReleaseId,
                ReleaseTitle = t.Release.Title,
                t.Release.ArtistDisplay,
                t.Release.Year,
                t.Release.DiscogsId,
                t.Sequence,
                t.Position,
                t.Title,
                t.Duration,
                t.TitleKey,
            })
            .ToListAsync(ct);

        var releaseDiscogsIds = page.Select(t => t.DiscogsId).Distinct().ToList();
        var ownedTitlesByRelease = new Dictionary<long, HashSet<string>>();
        if (releaseDiscogsIds.Count > 0)
        {
            var songs = await db.Songs.AsNoTracking()
                .Where(s => s.DeletedAtUtc == null && s.DiscogsReleaseId != null && releaseDiscogsIds.Contains(s.DiscogsReleaseId.Value))
                .Select(s => new { s.DiscogsReleaseId, s.Title })
                .ToListAsync(ct);
            foreach (var group in songs.GroupBy(s => s.DiscogsReleaseId!.Value))
            {
                ownedTitlesByRelease[group.Key] = group
                    .Select(s => CatalogKeys.TitleKey(s.Title))
                    .Where(k => k.Length > 0)
                    .ToHashSet(StringComparer.Ordinal);
            }
        }

        var items = page.Select(t => new CatalogTrackRowDto(
            t.ReleaseId, t.ReleaseTitle, t.ArtistDisplay, t.Year, t.Sequence, t.Position, t.Title, t.Duration,
            ownedTitlesByRelease.TryGetValue(t.DiscogsId, out var keys) && keys.Contains(CatalogKeys.TitleKey(t.Title)))).ToList();

        return new CatalogPage<CatalogTrackRowDto>(items, total, query.SafePage, query.SafePageSize);
    }

    /// <summary>~2 queries.</summary>
    public async Task<CatalogPage<CatalogStyleDto>> ListStylesAsync(CatalogListQuery query, CancellationToken ct = default)
    {
        var q = db.CatalogStyles.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var key = CatalogKeys.NameKey(query.Q);
            q = q.Where(s => s.NameKey.Contains(key) || s.Name.ToLower().Contains(query.Q.Trim().ToLower()));
        }

        var withCounts = q.Select(s => new
        {
            s.Name,
            s.NameKey,
            Count = db.CatalogReleaseStyles.Count(rs => rs.StyleId == s.Id),
        });

        var total = await withCounts.CountAsync(ct);
        var page = await withCounts
            .OrderByDescending(s => s.Count).ThenBy(s => s.NameKey)
            .Skip((query.SafePage - 1) * query.SafePageSize)
            .Take(query.SafePageSize)
            .ToListAsync(ct);

        return new CatalogPage<CatalogStyleDto>(
            page.Select(s => new CatalogStyleDto(s.Name, s.Count)).ToList(),
            total, query.SafePage, query.SafePageSize);
    }

    /// <summary>~4 queries (aggregations completed in memory — InMemory + Npgsql both fine).</summary>
    public async Task<CatalogFacetsDto> GetFacetsAsync(CancellationToken ct = default)
    {
        var styleRows = await db.CatalogReleaseStyles.AsNoTracking()
            .Select(rs => new { rs.ReleaseId, rs.Style.Name })
            .ToListAsync(ct);
        var styles = styleRows
            .GroupBy(r => r.Name)
            .Select(g => new CatalogStyleDto(g.Key, g.Select(x => x.ReleaseId).Distinct().Count()))
            .OrderByDescending(s => s.ReleaseCount).ThenBy(s => s.Name)
            .Take(FacetTop)
            .ToList();

        var formatRows = await db.CatalogReleaseFormats.AsNoTracking()
            .Select(f => new { f.ReleaseId, f.Name })
            .ToListAsync(ct);
        var formats = formatRows
            .GroupBy(f => f.Name)
            .Select(g => new CatalogFacetValueDto(g.Key, g.Select(x => x.ReleaseId).Distinct().Count()))
            .OrderByDescending(f => f.Count).ThenBy(f => f.Value)
            .Take(FacetTop)
            .ToList();

        var countryRows = await db.CatalogReleases.AsNoTracking()
            .Where(r => r.Country != null && r.Country != "")
            .Select(r => r.Country!)
            .ToListAsync(ct);
        var countries = countryRows
            .GroupBy(c => c)
            .Select(g => new CatalogFacetValueDto(g.Key, g.Count()))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Value)
            .Take(FacetTop)
            .ToList();

        var years = await db.CatalogReleases.AsNoTracking()
            .Where(r => r.Year != null)
            .Select(r => r.Year!.Value)
            .ToListAsync(ct);
        int? minYear = years.Count == 0 ? null : years.Min();
        int? maxYear = years.Count == 0 ? null : years.Max();

        return new CatalogFacetsDto(styles, formats, countries, minYear, maxYear);
    }

    /// <summary>~2–3 queries.</summary>
    public async Task<int?> FindArtistForLibraryNameAsync(string artistName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
            return null;

        var key = CatalogKeys.NameKey(artistName);
        if (key.Length == 0)
            return null;

        var songs = await db.Songs.AsNoTracking()
            .Where(s => s.DeletedAtUtc == null && s.DiscogsArtistId != null)
            .Select(s => new { s.DiscogsArtistId, s.AlbumArtist, s.Artist })
            .ToListAsync(ct);

        var match = songs
            .Where(s => CatalogKeys.NameKey(string.IsNullOrWhiteSpace(s.AlbumArtist) ? s.Artist : s.AlbumArtist) == key)
            .GroupBy(s => s.DiscogsArtistId!.Value)
            .Select(g => new { DiscogsId = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.DiscogsId)
            .FirstOrDefault();

        if (match is not null)
        {
            var id = await db.CatalogArtists.AsNoTracking()
                .Where(a => a.DiscogsId == match.DiscogsId)
                .Select(a => (int?)a.Id)
                .FirstOrDefaultAsync(ct);
            if (id is not null)
                return id;
        }

        var decision = await db.CatalogIdentityDecisions.AsNoTracking()
            .Where(d => d.SubjectKind == CatalogIdentitySubject.LibraryArtist
                        && d.SubjectKey == key
                        && d.TargetKind == CatalogEntityKind.Artist
                        && (d.Status == CatalogIdentityStatus.AutoAccepted || d.Status == CatalogIdentityStatus.Chosen))
            .OrderByDescending(d => d.DecidedAtUtc ?? d.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (decision is null)
            return null;

        return await db.CatalogArtists.AsNoTracking()
            .Where(a => a.DiscogsId == decision.DiscogsId)
            .Select(a => (int?)a.Id)
            .FirstOrDefaultAsync(ct);
    }

    // --- filters / sorts ---

    private async Task<IQueryable<CatalogRelease>> FilterReleasesAsync(
        IQueryable<CatalogRelease> q, CatalogReleaseQuery query, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var key = CatalogKeys.TitleKey(query.Q);
            var raw = query.Q.Trim().ToLower();
            q = q.Where(r => r.TitleKey.Contains(key)
                             || (r.ArtistKey != null && r.ArtistKey.Contains(key))
                             || r.Title.ToLower().Contains(raw));
        }

        if (query.ArtistId is int artistId)
        {
            var discogsId = await db.CatalogArtists.AsNoTracking()
                .Where(a => a.Id == artistId).Select(a => (long?)a.DiscogsId).FirstOrDefaultAsync(ct);
            if (discogsId is null)
                return q.Where(_ => false);

            q = q.Where(r =>
                r.Credits.Any(c => c.ArtistDiscogsId == discogsId && c.Kind == CatalogCreditKind.Main)
                || db.CatalogDiscographyEntries.Any(e =>
                    e.OwnerKind == CatalogDiscographyOwner.Artist
                    && e.OwnerId == artistId
                    && (e.ReleaseId == r.Id || (r.MasterId != null && e.MasterId == r.MasterId))));
        }

        if (query.LabelId is int labelId)
        {
            var labelDiscogsId = await db.CatalogLabels.AsNoTracking()
                .Where(l => l.Id == labelId).Select(l => (long?)l.DiscogsId).FirstOrDefaultAsync(ct);
            if (labelDiscogsId is null)
                return q.Where(_ => false);
            q = q.Where(r => r.Labels.Any(l =>
                l.LabelDiscogsId == labelDiscogsId && l.Kind == CatalogReleaseLabelKind.Label));
        }

        if (query.MasterId is int masterId)
            q = q.Where(r => r.MasterId == masterId);

        if (!string.IsNullOrWhiteSpace(query.Style))
        {
            var styleKey = CatalogKeys.NameKey(query.Style);
            q = q.Where(r => r.Styles.Any(s => s.Style.NameKey == styleKey));
        }

        if (!string.IsNullOrWhiteSpace(query.Format))
        {
            var format = query.Format.Trim();
            q = q.Where(r => r.Formats.Any(f => f.Name == format)
                             || (r.FormatSummary != null && r.FormatSummary.Contains(format)));
        }

        if (!string.IsNullOrWhiteSpace(query.Country))
            q = q.Where(r => r.Country == query.Country);

        if (query.YearFrom is int yf)
            q = q.Where(r => r.Year != null && r.Year >= yf);
        if (query.YearTo is int yt)
            q = q.Where(r => r.Year != null && r.Year <= yt);

        if (query.Owned is bool owned)
        {
            if (owned)
            {
                q = q.Where(r => db.Songs.Any(s =>
                    s.DeletedAtUtc == null && s.DiscogsReleaseId == r.DiscogsId));
            }
            else
            {
                q = q.Where(r => !db.Songs.Any(s =>
                    s.DeletedAtUtc == null && s.DiscogsReleaseId == r.DiscogsId));
            }
        }

        return q;
    }

    private async Task<IQueryable<CatalogMaster>> FilterMastersAsync(
        IQueryable<CatalogMaster> q, CatalogReleaseQuery query, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var key = CatalogKeys.TitleKey(query.Q);
            var raw = query.Q.Trim().ToLower();
            q = q.Where(m => m.TitleKey.Contains(key)
                             || (m.ArtistKey != null && m.ArtistKey.Contains(key))
                             || m.Title.ToLower().Contains(raw));
        }

        if (query.ArtistId is int artistId)
        {
            var discogsId = await db.CatalogArtists.AsNoTracking()
                .Where(a => a.Id == artistId).Select(a => (long?)a.DiscogsId).FirstOrDefaultAsync(ct);
            if (discogsId is null)
                return q.Where(_ => false);

            q = q.Where(m =>
                db.CatalogDiscographyEntries.Any(e =>
                    e.OwnerKind == CatalogDiscographyOwner.Artist
                    && e.OwnerId == artistId
                    && (e.MasterId == m.Id
                        || (e.ReleaseId != null && db.CatalogReleases.Any(r => r.Id == e.ReleaseId && r.MasterId == m.Id))))
                || db.CatalogCredits.Any(c =>
                    c.ArtistDiscogsId == discogsId
                    && c.Kind == CatalogCreditKind.Main
                    && c.Release.MasterId == m.Id));
        }

        if (query.LabelId is int labelId)
        {
            var labelDiscogsId = await db.CatalogLabels.AsNoTracking()
                .Where(l => l.Id == labelId).Select(l => (long?)l.DiscogsId).FirstOrDefaultAsync(ct);
            if (labelDiscogsId is null)
                return q.Where(_ => false);
            q = q.Where(m => db.CatalogReleaseLabels.Any(l =>
                l.LabelDiscogsId == labelDiscogsId
                && l.Kind == CatalogReleaseLabelKind.Label
                && l.Release.MasterId == m.Id));
        }

        if (query.MasterId is int masterId)
            q = q.Where(m => m.Id == masterId);

        if (!string.IsNullOrWhiteSpace(query.Style))
        {
            var styleKey = CatalogKeys.NameKey(query.Style);
            q = q.Where(m => m.Styles.Any(s => s.Style.NameKey == styleKey));
        }

        if (!string.IsNullOrWhiteSpace(query.Format))
        {
            var format = query.Format.Trim();
            q = q.Where(m => db.CatalogReleases.Any(r =>
                r.MasterId == m.Id
                && (r.Formats.Any(f => f.Name == format)
                    || (r.FormatSummary != null && r.FormatSummary.Contains(format)))));
        }

        if (!string.IsNullOrWhiteSpace(query.Country))
            q = q.Where(m => db.CatalogReleases.Any(r => r.MasterId == m.Id && r.Country == query.Country));

        if (query.YearFrom is int yf)
            q = q.Where(m => m.Year != null && m.Year >= yf);
        if (query.YearTo is int yt)
            q = q.Where(m => m.Year != null && m.Year <= yt);

        if (query.Owned is bool owned)
        {
            if (owned)
            {
                q = q.Where(m => db.Songs.Any(s =>
                    s.DeletedAtUtc == null && s.DiscogsMasterId == m.DiscogsId));
            }
            else
            {
                q = q.Where(m => !db.Songs.Any(s =>
                    s.DeletedAtUtc == null && s.DiscogsMasterId == m.DiscogsId));
            }
        }

        return q;
    }

    private static IQueryable<CatalogRelease> ApplyReleaseSort(IQueryable<CatalogRelease> q, string? sort) =>
        (sort ?? "year") switch
        {
            "-year" => q.OrderByDescending(r => r.Year).ThenBy(r => r.TitleKey).ThenBy(r => r.Id),
            "title" => q.OrderBy(r => r.TitleKey).ThenBy(r => r.Id),
            "added" => q.OrderByDescending(r => r.CreatedAtUtc).ThenBy(r => r.Id),
            _ => q.OrderBy(r => r.Year).ThenBy(r => r.TitleKey).ThenBy(r => r.Id),
        };

    private static IQueryable<CatalogMaster> ApplyMasterSort(IQueryable<CatalogMaster> q, string? sort) =>
        (sort ?? "year") switch
        {
            "-year" => q.OrderByDescending(m => m.Year).ThenBy(m => m.TitleKey).ThenBy(m => m.Id),
            "title" => q.OrderBy(m => m.TitleKey).ThenBy(m => m.Id),
            "added" => q.OrderByDescending(m => m.CreatedAtUtc).ThenBy(m => m.Id),
            _ => q.OrderBy(m => m.Year).ThenBy(m => m.TitleKey).ThenBy(m => m.Id),
        };

    // --- ownership / media helpers ---

    private async Task<HashSet<long>> OwnedReleaseDiscogsIdsAsync(IReadOnlyList<long> discogsIds, CancellationToken ct)
    {
        if (discogsIds.Count == 0)
            return [];
        var found = await db.Songs.AsNoTracking()
            .Where(s => s.DeletedAtUtc == null && s.DiscogsReleaseId != null && discogsIds.Contains(s.DiscogsReleaseId.Value))
            .Select(s => s.DiscogsReleaseId!.Value)
            .Distinct()
            .ToListAsync(ct);
        return found.ToHashSet();
    }

    private async Task<HashSet<long>> OwnedMasterDiscogsIdsAsync(IReadOnlyList<long> discogsIds, CancellationToken ct)
    {
        if (discogsIds.Count == 0)
            return [];
        var found = await db.Songs.AsNoTracking()
            .Where(s => s.DeletedAtUtc == null && s.DiscogsMasterId != null && discogsIds.Contains(s.DiscogsMasterId.Value))
            .Select(s => s.DiscogsMasterId!.Value)
            .Distinct()
            .ToListAsync(ct);
        return found.ToHashSet();
    }

    private async Task<HashSet<string>> OwnedTitleKeysForReleaseAsync(long releaseDiscogsId, CancellationToken ct)
    {
        var titles = await db.Songs.AsNoTracking()
            .Where(s => s.DeletedAtUtc == null && s.DiscogsReleaseId == releaseDiscogsId)
            .Select(s => s.Title)
            .ToListAsync(ct);
        return titles.Select(CatalogKeys.TitleKey).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
    }

    private async Task<Dictionary<int, IReadOnlyList<string>>> StylesForReleasesAsync(
        IReadOnlyList<int> releaseIds, CancellationToken ct)
    {
        if (releaseIds.Count == 0)
            return [];
        var rows = await db.CatalogReleaseStyles.AsNoTracking()
            .Where(rs => releaseIds.Contains(rs.ReleaseId))
            .OrderBy(rs => rs.Sequence)
            .Select(rs => new { rs.ReleaseId, rs.Style.Name })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.ReleaseId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Name).ToList());
    }

    private async Task<Dictionary<int, IReadOnlyList<string>>> StylesForMastersAsync(
        IReadOnlyList<int> masterIds, CancellationToken ct)
    {
        if (masterIds.Count == 0)
            return [];
        var rows = await db.CatalogMasterStyles.AsNoTracking()
            .Where(ms => masterIds.Contains(ms.MasterId))
            .OrderBy(ms => ms.Sequence)
            .Select(ms => new { ms.MasterId, ms.Style.Name })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.MasterId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Name).ToList());
    }

    private async Task<Dictionary<int, string?>> PrimaryThumbnailsAsync(
        CatalogEntityKind kind, IReadOnlyList<int> entityIds, CancellationToken ct)
    {
        if (entityIds.Count == 0)
            return [];
        var images = await db.CatalogImages.AsNoTracking()
            .Where(i => i.EntityKind == kind && entityIds.Contains(i.EntityId)
                        && i.Status != CatalogImageStatus.Retired)
            .OrderBy(i => i.Sequence)
            .ToListAsync(ct);

        var map = new Dictionary<int, string?>();
        foreach (var group in images.GroupBy(i => i.EntityId))
        {
            var primary = group.FirstOrDefault(i => i.Type.Equals("primary", StringComparison.OrdinalIgnoreCase))
                          ?? group.First();
            var dto = CatalogDtoMapper.MapImage(primary);
            map[group.Key] = dto.Url ?? dto.ThumbnailUrl;
        }
        return map;
    }

    private Task<List<CatalogUrl>> LoadUrlsAsync(CatalogEntityKind kind, int entityId, CancellationToken ct) =>
        db.CatalogUrls.AsNoTracking()
            .Where(u => u.EntityKind == kind && u.EntityId == entityId)
            .OrderBy(u => u.Sequence)
            .ToListAsync(ct);

    private Task<List<CatalogImage>> LoadImagesAsync(CatalogEntityKind kind, int entityId, CancellationToken ct) =>
        db.CatalogImages.AsNoTracking()
            .Where(i => i.EntityKind == kind && i.EntityId == entityId && i.Status != CatalogImageStatus.Retired)
            .OrderBy(i => i.Sequence)
            .ToListAsync(ct);

    private Task<List<CatalogExternalId>> LoadExternalIdsAsync(CatalogEntityKind kind, int entityId, CancellationToken ct) =>
        db.CatalogExternalIds.AsNoTracking()
            .Where(e => e.EntityKind == kind && e.EntityId == entityId)
            .OrderBy(e => e.Provider).ThenBy(e => e.ExternalId)
            .ToListAsync(ct);

    private static string? NullThumb(string? thumb) =>
        string.IsNullOrWhiteSpace(thumb) ? null : thumb;
}
