using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Discogs;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Catalog.Identity;

public sealed class CatalogIdentityService(
    MusicHoarderDbContext db,
    IDiscogsClient discogs,
    ICatalogFetcher fetcher,
    IMusicBrainzWebService musicBrainz,
    IOptions<DiscogsOptions> options) : ICatalogIdentityService
{
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web);
    private const string DefaultChangeSource = "discogs-identity";

    public async Task<CatalogIdentityView> GetArtistIdentityAsync(Guid ownerId, string artistName, CancellationToken ct = default)
    {
        var key = DiscogsMatchScoring.ArtistSubjectKey(artistName);
        return await BuildViewAsync(ownerId, CatalogIdentitySubject.LibraryArtist, key, artistName.Trim(), ct);
    }

    public async Task<CatalogIdentityView> ResolveArtistAsync(
        Guid ownerId, string artistName, int? runId, bool dryRun, CancellationToken ct = default)
    {
        var display = artistName.Trim();
        var key = DiscogsMatchScoring.ArtistSubjectKey(display);
        if (string.IsNullOrEmpty(key))
            return EmptyView(CatalogIdentitySubject.LibraryArtist, key, display);

        var existing = await GetActiveAcceptedAsync(ownerId, CatalogIdentitySubject.LibraryArtist, key, ct);
        if (existing is not null)
            return await BuildViewAsync(ownerId, CatalogIdentitySubject.LibraryArtist, key, display, ct);

        var songs = await LoadArtistSongsAsync(ownerId, display, ct);
        var changeSource = await ResolveChangeSourceAsync(runId, ct);

        // 1) MusicBrainz artist url-rels; accepted only while every subject MBID agrees on one Discogs artist
        var relationHits = new List<(string Mbid, string Url, long DiscogsId)>();
        foreach (var mbid in CollectArtistMbids(songs, key))
        {
            var rels = await musicBrainz.GetArtistUrlRelationsAsync(mbid, ct);
            foreach (var rel in rels.Where(r => string.Equals(r.Type, "discogs", StringComparison.OrdinalIgnoreCase)))
            {
                if (!DiscogsUrl.TryParse(rel.Url, out var kind, out var id) || kind != DiscogsResourceKind.Artist)
                    continue;
                relationHits.Add((mbid, rel.Url, id));
            }
        }

        var relationTargets = relationHits.GroupBy(h => h.DiscogsId).ToList();
        if (relationTargets.Count == 1)
        {
            var (mbid, url, id) = relationHits[0];
            return await AcceptArtistAsync(
                ownerId, key, display, id, CatalogIdentityMethod.MusicBrainzRelation, 1.0,
                Evidence(new { mbid, url, discogsId = id }),
                runId, dryRun, changeSource, ct);
        }

        if (relationTargets.Count > 1)
        {
            await RecordConflictingRelationsAsync(ownerId, key, display, relationTargets, runId, ct);
            return await BuildViewAsync(ownerId, CatalogIdentitySubject.LibraryArtist, key, display, ct);
        }

        // 2) Linked releases: majority of this artist's resolved albums share one Discogs artist id
        var linkedArtistId = await TryLinkedReleasesArtistAsync(ownerId, display, ct);
        if (linkedArtistId is long linkedId)
        {
            return await AcceptArtistAsync(
                ownerId, key, display, linkedId, CatalogIdentityMethod.LinkedReleases, 0.95,
                Evidence(new { discogsArtistId = linkedId, source = "majority-albums" }),
                runId, dryRun, changeSource, ct);
        }

        // 3) Authenticated name search with exact name + album corroboration
        if (discogs.IsAuthenticated)
        {
            var page = await discogs.SearchAsync(new DiscogsSearchQuery(Type: "artist", Query: display), page: 1, perPage: 25, ct);
            var exact = page.Items
                .Where(i => string.Equals(i.Type, "artist", StringComparison.OrdinalIgnoreCase)
                    && DiscogsMatchScoring.ArtistsEqual(display, StripSearchArtistTitle(i.Title)))
                .ToList();

            var corroborated = new List<(DiscogsSearchResult Hit, string Evidence)>();
            foreach (var hit in exact)
            {
                var albums = songs
                    .Select(s => (s.AlbumArtist ?? s.Artist, s.Album))
                    .Where(a => !string.IsNullOrWhiteSpace(a.Item1) && !string.IsNullOrWhiteSpace(a.Album))
                    .DistinctBy(a => DiscogsMatchScoring.AlbumSubjectKey(a.Item1!, a.Album!))
                    .Take(8)
                    .ToList();

                var releases = await discogs.GetArtistReleasesAsync(hit.Id, page: 1, perPage: 100, ct);
                var titles = (releases?.Items ?? [])
                    .Select(e => CatalogKeys.TitleKey(e.Title))
                    .Where(t => t.Length > 0)
                    .ToHashSet(StringComparer.Ordinal);

                var matchedAlbum = albums.FirstOrDefault(a => titles.Contains(CatalogKeys.TitleKey(a.Album)));
                if (matchedAlbum.Album is not null)
                {
                    corroborated.Add((hit, Evidence(new
                    {
                        discogsId = hit.Id,
                        name = hit.Title,
                        corroboratingAlbum = matchedAlbum.Album,
                    })));
                }
            }

            if (corroborated.Count == 1)
            {
                var (hit, evidence) = corroborated[0];
                return await AcceptArtistAsync(
                    ownerId, key, display, hit.Id, CatalogIdentityMethod.NameSearch, 0.92,
                    evidence, runId, dryRun, changeSource, ct);
            }

            if (exact.Count > 0)
            {
                await ClearOpenCandidatesAsync(ownerId, CatalogIdentitySubject.LibraryArtist, key, ct);
                var top = exact.Take(5).ToList();
                foreach (var hit in top)
                {
                    db.CatalogIdentityDecisions.Add(new CatalogIdentityDecision
                    {
                        OwnerUserId = ownerId,
                        SubjectKind = CatalogIdentitySubject.LibraryArtist,
                        SubjectKey = key,
                        SubjectDisplay = display,
                        TargetKind = CatalogEntityKind.Artist,
                        DiscogsId = hit.Id,
                        TargetName = hit.Title,
                        Method = CatalogIdentityMethod.NameSearch,
                        Confidence = DiscogsMatchScoring.ArtistsEqual(display, StripSearchArtistTitle(hit.Title)) ? 0.7 : 0.4,
                        Status = CatalogIdentityStatus.Candidate,
                        EvidenceJson = Evidence(new { discogsId = hit.Id, title = hit.Title, note = "needs-corroboration" }),
                        RunId = runId,
                        CreatedAtUtc = DateTime.UtcNow,
                    });
                }

                await db.SaveChangesAsync(ct);
            }
        }

        return await BuildViewAsync(ownerId, CatalogIdentitySubject.LibraryArtist, key, display, ct);
    }

    public async Task<CatalogIdentityView> ResolveAlbumAsync(
        Guid ownerId, string albumArtist, string album, int? runId, bool dryRun, CancellationToken ct = default)
    {
        var displayArtist = albumArtist.Trim();
        var displayAlbum = album.Trim();
        var key = DiscogsMatchScoring.AlbumSubjectKey(displayArtist, displayAlbum);
        var subjectDisplay = $"{displayArtist} – {displayAlbum}";
        if (string.IsNullOrEmpty(CatalogKeys.NameKey(displayArtist)) || string.IsNullOrEmpty(CatalogKeys.TitleKey(displayAlbum)))
            return EmptyView(CatalogIdentitySubject.LibraryAlbum, key, subjectDisplay);

        var songs = await LoadAlbumSongsAsync(ownerId, displayArtist, displayAlbum, ct);
        if (songs.Count == 0)
            return EmptyView(CatalogIdentitySubject.LibraryAlbum, key, subjectDisplay);

        // Never overwrite a Chosen release with a different id.
        if (await HasChosenDifferentReleaseAsync(ownerId, key, ct))
            return await BuildViewAsync(ownerId, CatalogIdentitySubject.LibraryAlbum, key, subjectDisplay, ct);

        var existing = await GetActiveAcceptedAsync(ownerId, CatalogIdentitySubject.LibraryAlbum, key, ct);
        if (existing is not null && existing.Status is CatalogIdentityStatus.AutoAccepted or CatalogIdentityStatus.Chosen)
            return await BuildViewAsync(ownerId, CatalogIdentitySubject.LibraryAlbum, key, subjectDisplay, ct);

        var changeSource = await ResolveChangeSourceAsync(runId, ct);
        var hint = BuildAlbumHint(displayArtist, displayAlbum, songs);
        var candidates = new List<(long ReleaseId, long? MasterId, string? Name, CatalogIdentityMethod Method, double Confidence, string Evidence)>();

        // 1) MB release url-rels
        foreach (var releaseMbid in songs.Select(s => s.MusicBrainzReleaseId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var rels = await musicBrainz.GetReleaseUrlRelationsAsync(releaseMbid!, ct);
            foreach (var rel in rels.Where(r => string.Equals(r.Type, "discogs", StringComparison.OrdinalIgnoreCase)))
            {
                if (!DiscogsUrl.TryParse(rel.Url, out var kind, out var id))
                    continue;
                if (kind == DiscogsResourceKind.Release)
                {
                    candidates.Add((id, null, null, CatalogIdentityMethod.MusicBrainzRelation, 1.0,
                        Evidence(new { mbid = releaseMbid, url = rel.Url, discogsId = id, kind = "release" })));
                }
                else if (kind == DiscogsResourceKind.Master)
                {
                    var releaseId = await ResolveMasterToReleaseAsync(id, ct);
                    if (releaseId is long rid)
                    {
                        candidates.Add((rid, id, null, CatalogIdentityMethod.MusicBrainzRelation, 1.0,
                            Evidence(new { mbid = releaseMbid, url = rel.Url, masterId = id, releaseId = rid })));
                    }
                }
            }
        }

        // 2) MB release-group url-rels → master
        foreach (var rgMbid in songs.Select(s => s.MusicBrainzReleaseGroupId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var rels = await musicBrainz.GetReleaseGroupUrlRelationsAsync(rgMbid!, ct);
            foreach (var rel in rels.Where(r => string.Equals(r.Type, "discogs", StringComparison.OrdinalIgnoreCase)))
            {
                if (!DiscogsUrl.TryParse(rel.Url, out var kind, out var id) || kind != DiscogsResourceKind.Master)
                    continue;
                var releaseId = await ResolveMasterToReleaseAsync(id, ct);
                if (releaseId is long rid)
                {
                    candidates.Add((rid, id, null, CatalogIdentityMethod.MusicBrainzRelation, 1.0,
                        Evidence(new { mbid = rgMbid, url = rel.Url, masterId = id, releaseId = rid })));
                }
            }
        }

        if (discogs.IsAuthenticated)
        {
            // 3) Barcode
            var barcode = songs.Select(s => s.Upc).FirstOrDefault(CatalogKeys.IsUsableBarcode);
            if (barcode is not null)
            {
                var page = await discogs.SearchAsync(new DiscogsSearchQuery(Type: "release", Barcode: barcode), page: 1, perPage: 10, ct);
                foreach (var hit in page.Items.Where(i => string.Equals(i.Type, "release", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!DiscogsMatchScoring.SearchHitBarcodeMatches(hint, hit)
                        || CorroboratedSearchHitScore(hint, hit) is not double score)
                        continue;
                    var conf = Math.Max(0.95, score);
                    candidates.Add((hit.Id, hit.MasterId, hit.Title, CatalogIdentityMethod.Barcode, conf,
                        Evidence(new { barcode, discogsId = hit.Id, title = hit.Title })));
                }
            }

            // 4) Label + catno
            var label = songs.Select(s => s.Label).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            var catno = songs.Select(s => s.CatalogNumber).FirstOrDefault(c =>
                !string.IsNullOrWhiteSpace(c) && !string.Equals(c.Trim(), "none", StringComparison.OrdinalIgnoreCase));
            if (label is not null && catno is not null)
            {
                var page = await discogs.SearchAsync(
                    new DiscogsSearchQuery(Type: "release", Label: label, Catno: catno), page: 1, perPage: 10, ct);
                foreach (var hit in page.Items.Where(i => string.Equals(i.Type, "release", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!DiscogsMatchScoring.SearchHitCatalogNumberMatches(hint, hit)
                        || CorroboratedSearchHitScore(hint, hit) is not double score)
                        continue;
                    var conf = Math.Max(0.93, score);
                    candidates.Add((hit.Id, hit.MasterId, hit.Title, CatalogIdentityMethod.CatalogNumber, conf,
                        Evidence(new { label, catno, discogsId = hit.Id, title = hit.Title })));
                }
            }

            // 5) Artist + release_title with tracklist scoring
            var search = await discogs.SearchAsync(
                new DiscogsSearchQuery(Type: "release", Artist: displayArtist, ReleaseTitle: displayAlbum),
                page: 1, perPage: 25, ct);
            foreach (var hit in search.Items.Where(i => string.Equals(i.Type, "release", StringComparison.OrdinalIgnoreCase)).Take(10))
            {
                var doc = await discogs.GetReleaseAsync(hit.Id, ct);
                if (doc is null)
                    continue;
                var conf = DiscogsMatchScoring.ScoreRelease(hint, doc.Value);
                if (conf <= 0)
                    continue;
                candidates.Add((hit.Id, doc.Value.MasterId ?? hit.MasterId, doc.Value.Title,
                    CatalogIdentityMethod.Tracklist, conf,
                    Evidence(new
                    {
                        discogsId = hit.Id,
                        title = doc.Value.Title,
                        confidence = conf,
                        trackCount = DiscogsMatchScoring.FlattenTracks(doc.Value.Tracklist).Count(),
                    })));
            }
        }

        var ranked = candidates
            .GroupBy(c => c.ReleaseId)
            .Select(g => g.OrderByDescending(x => x.Confidence).First())
            .OrderByDescending(c => c.Confidence)
            .ThenBy(c => c.ReleaseId)
            .ToList();

        var threshold = options.Value.AutoAcceptConfidence;
        var best = ranked.FirstOrDefault();
        if (best.Confidence >= threshold && best.ReleaseId > 0)
        {
            return await AcceptAlbumAsync(
                ownerId, key, subjectDisplay, songs, best.ReleaseId, best.MasterId, best.Name,
                best.Method, best.Confidence, best.Evidence, runId, dryRun, changeSource, ct);
        }

        await ClearOpenCandidatesAsync(ownerId, CatalogIdentitySubject.LibraryAlbum, key, ct);
        foreach (var c in ranked.Take(5))
        {
            db.CatalogIdentityDecisions.Add(new CatalogIdentityDecision
            {
                OwnerUserId = ownerId,
                SubjectKind = CatalogIdentitySubject.LibraryAlbum,
                SubjectKey = key,
                SubjectDisplay = subjectDisplay,
                TargetKind = CatalogEntityKind.Release,
                DiscogsId = c.ReleaseId,
                TargetName = c.Name,
                Method = c.Method,
                Confidence = c.Confidence,
                Status = CatalogIdentityStatus.Candidate,
                EvidenceJson = c.Evidence,
                RunId = runId,
                CreatedAtUtc = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync(ct);
        return await BuildViewAsync(ownerId, CatalogIdentitySubject.LibraryAlbum, key, subjectDisplay, ct);
    }

    public async Task<CatalogIdentityView> ChooseAsync(
        Guid ownerId,
        CatalogIdentitySubject subject,
        string subjectDisplay,
        CatalogEntityKind target,
        long discogsId,
        string decidedBy,
        CancellationToken ct = default)
    {
        var key = subject == CatalogIdentitySubject.LibraryArtist
            ? DiscogsMatchScoring.ArtistSubjectKey(subjectDisplay)
            : ParseAlbumDisplayKey(subjectDisplay);

        await SupersedeActiveAsync(ownerId, subject, key, ct);

        var now = DateTime.UtcNow;
        var decision = new CatalogIdentityDecision
        {
            OwnerUserId = ownerId,
            SubjectKind = subject,
            SubjectKey = key,
            SubjectDisplay = subjectDisplay.Trim(),
            TargetKind = target,
            DiscogsId = discogsId,
            Method = CatalogIdentityMethod.Manual,
            Confidence = 1.0,
            Status = CatalogIdentityStatus.Chosen,
            EvidenceJson = Evidence(new { chosen = true, discogsId, decidedBy }),
            CreatedAtUtc = now,
            DecidedAtUtc = now,
            DecidedBy = decidedBy,
        };
        db.CatalogIdentityDecisions.Add(decision);
        await db.SaveChangesAsync(ct);

        if (subject == CatalogIdentitySubject.LibraryArtist && target == CatalogEntityKind.Artist)
        {
            await EnsureArtistCatalogLinkAsync(ownerId, subjectDisplay.Trim(), discogsId, dryRun: false, ct);
        }
        else if (subject == CatalogIdentitySubject.LibraryAlbum)
        {
            var (albumArtist, album) = SplitAlbumDisplay(subjectDisplay);
            var songs = await LoadAlbumSongsAsync(ownerId, albumArtist, album, ct);
            var releaseId = discogsId;
            long? masterId = null;
            if (target == CatalogEntityKind.Master)
            {
                masterId = discogsId;
                releaseId = await ResolveMasterToReleaseAsync(discogsId, ct) ?? discogsId;
            }

            var release = await fetcher.EnsureReleaseAsync(releaseId, force: false, ct);
            masterId ??= release?.MasterDiscogsId;
            var artistId = release?.Credits
                .Where(c => c.Kind == CatalogCreditKind.Main && c.TrackSequence is null)
                .OrderBy(c => c.Sequence)
                .Select(c => (long?)c.ArtistDiscogsId)
                .FirstOrDefault();

            await ApplyAlbumToSongsAsync(songs, releaseId, masterId, DefaultChangeSource, 1.0, ct, artistId);
            await db.SaveChangesAsync(ct);
        }

        return await BuildViewAsync(ownerId, subject, key, subjectDisplay.Trim(), ct);
    }

    public async Task<bool> RejectAsync(Guid ownerId, int decisionId, string decidedBy, CancellationToken ct = default)
    {
        var decision = await db.CatalogIdentityDecisions
            .FirstOrDefaultAsync(d => d.Id == decisionId && d.OwnerUserId == ownerId, ct);
        if (decision is null)
            return false;
        if (decision.Status == CatalogIdentityStatus.Rejected)
            return true;

        var now = DateTime.UtcNow;
        if (decision.Status is CatalogIdentityStatus.AutoAccepted or CatalogIdentityStatus.Chosen
            && decision.SubjectKind == CatalogIdentitySubject.LibraryAlbum)
        {
            await RevertAlbumAcceptanceAsync(ownerId, decision, now, ct);
        }

        decision.Status = CatalogIdentityStatus.Rejected;
        decision.DecidedAtUtc = now;
        decision.DecidedBy = decidedBy;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Undoes the song ids an accepted album decision wrote. A change belongs to the acceptance when it
    /// set the decision's own id (release, or master for master targets) or was written in the same apply
    /// batch (same source and timestamp). A field is only restored while it still holds the value the
    /// change wrote, so later user edits survive.
    /// </summary>
    private async Task RevertAlbumAcceptanceAsync(Guid ownerId, CatalogIdentityDecision decision, DateTime now, CancellationToken ct)
    {
        var songs = (await LoadAlbumSongsByKeyAsync(ownerId, decision.SubjectKey, includeDeleted: true, ct))
            .ToDictionary(s => s.Id);
        if (songs.Count == 0)
            return;

        var songIds = songs.Keys.ToList();
        var changes = await db.SongMetadataChanges
            .IgnoreQueryFilters()
            .Where(c => songIds.Contains(c.SongId)
                && c.RevertedAtUtc == null
                && c.AppliedAtUtc != null
                && (c.FieldName == nameof(SongMetadata.DiscogsReleaseId)
                    || c.FieldName == nameof(SongMetadata.DiscogsMasterId)
                    || c.FieldName == nameof(SongMetadata.DiscogsArtistId))
                && (c.Source == DefaultChangeSource || c.Source.StartsWith("discogs-run:")))
            .ToListAsync(ct);

        var anchorField = decision.TargetKind == CatalogEntityKind.Master
            ? nameof(SongMetadata.DiscogsMasterId)
            : nameof(SongMetadata.DiscogsReleaseId);
        var anchorValue = decision.DiscogsId.ToString();

        foreach (var songChanges in changes.GroupBy(c => c.SongId))
        {
            var song = songs[songChanges.Key];
            var batches = songChanges
                .Where(c => c.FieldName == anchorField && c.NewValue == anchorValue)
                .Select(c => (c.Source, c.CreatedAtUtc))
                .ToHashSet();
            if (batches.Count == 0)
                continue;

            var owned = songChanges.Where(c => batches.Contains((c.Source, c.CreatedAtUtc)));
            foreach (var field in owned.GroupBy(c => c.FieldName))
            {
                var current = ReadDiscogsField(song, field.Key);
                var change = field
                    .Where(c => string.Equals(c.NewValue, current, StringComparison.Ordinal))
                    .OrderByDescending(c => c.CreatedAtUtc)
                    .ThenByDescending(c => c.Id)
                    .FirstOrDefault();
                if (change is null)
                    continue;

                WriteDiscogsField(song, field.Key, change.OldValue);
                change.RevertedAtUtc = now;
            }
        }
    }

    private static string? ReadDiscogsField(SongMetadata song, string fieldName) => fieldName switch
    {
        nameof(SongMetadata.DiscogsReleaseId) => song.DiscogsReleaseId?.ToString(),
        nameof(SongMetadata.DiscogsMasterId) => song.DiscogsMasterId?.ToString(),
        nameof(SongMetadata.DiscogsArtistId) => song.DiscogsArtistId?.ToString(),
        _ => null,
    };

    private static void WriteDiscogsField(SongMetadata song, string fieldName, string? value)
    {
        long? parsed = string.IsNullOrWhiteSpace(value) ? null : long.Parse(value);
        switch (fieldName)
        {
            case nameof(SongMetadata.DiscogsReleaseId):
                song.DiscogsReleaseId = parsed;
                break;
            case nameof(SongMetadata.DiscogsMasterId):
                song.DiscogsMasterId = parsed;
                break;
            case nameof(SongMetadata.DiscogsArtistId):
                song.DiscogsArtistId = parsed;
                break;
        }
    }

    public async Task<IReadOnlyList<CatalogIdentityDecisionDto>> ListReviewAsync(Guid ownerId, int limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 500);
        var rows = await db.CatalogIdentityDecisions
            .AsNoTracking()
            .Where(d => d.OwnerUserId == ownerId && d.Status == CatalogIdentityStatus.Candidate)
            .OrderByDescending(d => d.CreatedAtUtc)
            .Take(take * 5)
            .ToListAsync(ct);

        // One representative (highest confidence) per subject, then flatten top candidates.
        return rows
            .GroupBy(d => new { d.SubjectKind, d.SubjectKey })
            .SelectMany(g => g.OrderByDescending(x => x.Confidence).Take(5))
            .OrderByDescending(d => d.CreatedAtUtc)
            .Take(take)
            .Select(ToDecisionDto)
            .ToList();
    }

    // --- helpers ---

    private async Task<CatalogIdentityView> AcceptArtistAsync(
        Guid ownerId, string key, string display, long discogsId,
        CatalogIdentityMethod method, double confidence, string evidence,
        int? runId, bool dryRun, string changeSource, CancellationToken ct)
    {
        await ClearOpenCandidatesAsync(ownerId, CatalogIdentitySubject.LibraryArtist, key, ct);
        var now = DateTime.UtcNow;
        var status = dryRun ? CatalogIdentityStatus.Candidate : CatalogIdentityStatus.AutoAccepted;
        if (!dryRun)
            await SupersedeActiveAsync(ownerId, CatalogIdentitySubject.LibraryArtist, key, ct);

        var artist = dryRun ? null : await fetcher.EnsureArtistAsync(discogsId, withDiscography: true, force: false, ct);
        db.CatalogIdentityDecisions.Add(new CatalogIdentityDecision
        {
            OwnerUserId = ownerId,
            SubjectKind = CatalogIdentitySubject.LibraryArtist,
            SubjectKey = key,
            SubjectDisplay = display,
            TargetKind = CatalogEntityKind.Artist,
            DiscogsId = discogsId,
            TargetName = artist?.Name,
            Method = method,
            Confidence = confidence,
            Status = status,
            EvidenceJson = evidence,
            RunId = runId,
            CreatedAtUtc = now,
            DecidedAtUtc = dryRun ? null : now,
            DecidedBy = dryRun ? null : "system",
        });
        await db.SaveChangesAsync(ct);

        if (!dryRun)
            await EnsureArtistCatalogLinkAsync(ownerId, display, discogsId, dryRun: false, ct);

        return await BuildViewAsync(ownerId, CatalogIdentitySubject.LibraryArtist, key, display, ct);
    }

    private async Task<CatalogIdentityView> AcceptAlbumAsync(
        Guid ownerId, string key, string subjectDisplay, List<SongMetadata> songs,
        long releaseId, long? masterId, string? name,
        CatalogIdentityMethod method, double confidence, string evidence,
        int? runId, bool dryRun, string changeSource, CancellationToken ct)
    {
        await ClearOpenCandidatesAsync(ownerId, CatalogIdentitySubject.LibraryAlbum, key, ct);
        var now = DateTime.UtcNow;
        var status = dryRun ? CatalogIdentityStatus.Candidate : CatalogIdentityStatus.AutoAccepted;
        if (!dryRun)
            await SupersedeActiveAsync(ownerId, CatalogIdentitySubject.LibraryAlbum, key, ct);

        var release = await fetcher.EnsureReleaseAsync(releaseId, force: false, ct);
        var targetName = release?.Title ?? name;
        var resolvedMaster = masterId ?? release?.MasterDiscogsId;
        var artistId = release?.Credits
            .Where(c => c.Kind == CatalogCreditKind.Main && c.TrackSequence is null)
            .OrderBy(c => c.Sequence)
            .Select(c => (long?)c.ArtistDiscogsId)
            .FirstOrDefault();

        db.CatalogIdentityDecisions.Add(new CatalogIdentityDecision
        {
            OwnerUserId = ownerId,
            SubjectKind = CatalogIdentitySubject.LibraryAlbum,
            SubjectKey = key,
            SubjectDisplay = subjectDisplay,
            TargetKind = CatalogEntityKind.Release,
            DiscogsId = releaseId,
            TargetName = targetName,
            Method = method,
            Confidence = confidence,
            Status = status,
            EvidenceJson = evidence,
            RunId = runId,
            CreatedAtUtc = now,
            DecidedAtUtc = dryRun ? null : now,
            DecidedBy = dryRun ? null : "system",
        });

        if (!dryRun)
            await ApplyAlbumToSongsAsync(songs, releaseId, resolvedMaster, changeSource, confidence, ct, artistId);

        await db.SaveChangesAsync(ct);
        return await BuildViewAsync(ownerId, CatalogIdentitySubject.LibraryAlbum, key, subjectDisplay, ct);
    }

    private async Task ApplyAlbumToSongsAsync(
        List<SongMetadata> songs,
        long releaseId,
        long? masterId,
        string changeSource,
        double confidence,
        CancellationToken ct,
        long? artistId = null)
    {
        var now = DateTime.UtcNow;
        if (artistId is null)
        {
            var release = await db.CatalogReleases
                .AsNoTracking()
                .Include(r => r.Credits)
                .FirstOrDefaultAsync(r => r.DiscogsId == releaseId, ct);
            artistId = release?.Credits
                .Where(c => c.Kind == CatalogCreditKind.Main && c.TrackSequence is null)
                .OrderBy(c => c.Sequence)
                .Select(c => (long?)c.ArtistDiscogsId)
                .FirstOrDefault();
            masterId ??= release?.MasterDiscogsId;
        }

        foreach (var song in songs)
        {
            // Never overwrite a different release set by a Chosen decision path that already stamped it.
            if (song.DiscogsReleaseId is long existing && existing != releaseId)
            {
                var chosen = await db.CatalogIdentityDecisions.AsNoTracking().AnyAsync(d =>
                    d.OwnerUserId == song.OwnerUserId
                    && d.SubjectKind == CatalogIdentitySubject.LibraryAlbum
                    && d.Status == CatalogIdentityStatus.Chosen
                    && d.DiscogsId == existing, ct);
                if (chosen)
                    continue;
            }

            ApplyField(song, nameof(SongMetadata.DiscogsReleaseId), song.DiscogsReleaseId?.ToString(), releaseId.ToString(),
                v => song.DiscogsReleaseId = long.Parse(v!), changeSource, confidence, now);
            if (masterId is long mid)
            {
                ApplyField(song, nameof(SongMetadata.DiscogsMasterId), song.DiscogsMasterId?.ToString(), mid.ToString(),
                    v => song.DiscogsMasterId = long.Parse(v!), changeSource, confidence, now);
            }

            if (artistId is long aid)
            {
                ApplyField(song, nameof(SongMetadata.DiscogsArtistId), song.DiscogsArtistId?.ToString(), aid.ToString(),
                    v => song.DiscogsArtistId = long.Parse(v!), changeSource, confidence, now);
            }
        }
    }

    private void ApplyField(
        SongMetadata song,
        string fieldName,
        string? oldValue,
        string? newValue,
        Action<string?> assign,
        string source,
        double confidence,
        DateTime now)
    {
        if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
            return;

        assign(newValue);
        db.SongMetadataChanges.Add(new SongMetadataChange
        {
            SongId = song.Id,
            FieldName = fieldName,
            OldValue = oldValue,
            NewValue = newValue,
            Source = source,
            Confidence = confidence,
            CreatedAtUtc = now,
            AppliedAtUtc = now,
        });
    }

    private async Task EnsureArtistCatalogLinkAsync(Guid ownerId, string artistName, long discogsArtistId, bool dryRun, CancellationToken ct)
    {
        _ = ownerId;
        if (dryRun)
            return;

        var artist = await fetcher.EnsureArtistAsync(discogsArtistId, withDiscography: false, force: false, ct);
        if (artist is null)
            return;

        var artistKey = CatalogKeys.NameKey(artistName);
        var link = await db.ArtistCatalogLinks.AsNoTracking()
            .FirstOrDefaultAsync(l => l.ArtistKey == artistKey
                && (l.Status == ArtistCatalogLinkStatus.Linked || l.Status == ArtistCatalogLinkStatus.Chosen)
                && l.DeezerArtistId != null, ct);
        if (link?.DeezerArtistId is not { Length: > 0 } deezerId)
            return;

        var exists = await db.CatalogExternalIds.AnyAsync(e =>
            e.EntityKind == CatalogEntityKind.Artist
            && e.EntityId == artist.Id
            && e.Provider == "Deezer"
            && e.ExternalId == deezerId, ct);
        if (exists)
            return;

        db.CatalogExternalIds.Add(new CatalogExternalId
        {
            EntityKind = CatalogEntityKind.Artist,
            EntityId = artist.Id,
            Provider = "Deezer",
            ExternalId = deezerId,
            Source = "artist-link",
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task<long?> TryLinkedReleasesArtistAsync(Guid ownerId, string artistName, CancellationToken ct)
    {
        var songs = await LoadArtistSongsAsync(ownerId, artistName, ct);
        var subjectKey = DiscogsMatchScoring.ArtistSubjectKey(artistName);
        if (DestinationPathResolver.IsVariousArtistsSentinel(artistName))
            return null;

        var artistIds = songs
            .Where(s => CatalogKeys.NameKey(s.AlbumArtist) == subjectKey
                && s.DiscogsArtistId is > 0
                && s.DiscogsArtistId != DiscogsReleaseMatcher.VariousArtistsId
                && s.DiscogsReleaseId is > 0)
            .GroupBy(s => DiscogsMatchScoring.AlbumSubjectKey(s.AlbumArtist ?? s.Artist ?? "", s.Album ?? ""))
            .Where(g => g.Key.Length > 1)
            .Select(g => g.Select(s => s.DiscogsArtistId!.Value).First())
            .ToList();
        if (artistIds.Count == 0)
            return null;

        var majority = artistIds
            .GroupBy(id => id)
            .Select(g => (Id: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .First();
        return majority.Count * 2 > artistIds.Count ? majority.Id : null;
    }

    private async Task<long?> ResolveMasterToReleaseAsync(long masterId, CancellationToken ct)
    {
        var master = await fetcher.EnsureMasterAsync(masterId, withVersions: false, force: false, ct);
        if (master?.MainReleaseDiscogsId is long main)
            return main;

        var doc = await discogs.GetMasterAsync(masterId, ct);
        return doc?.Value.MainRelease;
    }

    private async Task<string> ResolveChangeSourceAsync(int? runId, CancellationToken ct)
    {
        if (runId is null)
            return DefaultChangeSource;
        var run = await db.CatalogBackfillRuns.AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == runId, ct);
        return run?.ChangeSource ?? DefaultChangeSource;
    }

    private async Task<CatalogIdentityDecision?> GetActiveAcceptedAsync(
        Guid ownerId, CatalogIdentitySubject subject, string key, CancellationToken ct) =>
        await db.CatalogIdentityDecisions
            .AsNoTracking()
            .Where(d => d.OwnerUserId == ownerId
                && d.SubjectKind == subject
                && d.SubjectKey == key
                && (d.Status == CatalogIdentityStatus.AutoAccepted || d.Status == CatalogIdentityStatus.Chosen))
            .OrderByDescending(d => d.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

    private async Task<bool> HasChosenDifferentReleaseAsync(Guid ownerId, string key, CancellationToken ct) =>
        await db.CatalogIdentityDecisions.AsNoTracking().AnyAsync(d =>
            d.OwnerUserId == ownerId
            && d.SubjectKind == CatalogIdentitySubject.LibraryAlbum
            && d.SubjectKey == key
            && d.Status == CatalogIdentityStatus.Chosen, ct);

    private async Task SupersedeActiveAsync(Guid ownerId, CatalogIdentitySubject subject, string key, CancellationToken ct)
    {
        var rows = await db.CatalogIdentityDecisions
            .Where(d => d.OwnerUserId == ownerId
                && d.SubjectKind == subject
                && d.SubjectKey == key
                && (d.Status == CatalogIdentityStatus.AutoAccepted
                    || d.Status == CatalogIdentityStatus.Chosen
                    || d.Status == CatalogIdentityStatus.Candidate))
            .ToListAsync(ct);
        foreach (var row in rows)
            row.Status = CatalogIdentityStatus.Superseded;
    }

    private async Task ClearOpenCandidatesAsync(Guid ownerId, CatalogIdentitySubject subject, string key, CancellationToken ct)
    {
        var rows = await db.CatalogIdentityDecisions
            .Where(d => d.OwnerUserId == ownerId
                && d.SubjectKind == subject
                && d.SubjectKey == key
                && d.Status == CatalogIdentityStatus.Candidate)
            .ToListAsync(ct);
        foreach (var row in rows)
            row.Status = CatalogIdentityStatus.Superseded;
    }

    private async Task<CatalogIdentityView> BuildViewAsync(
        Guid ownerId, CatalogIdentitySubject subject, string key, string display, CancellationToken ct)
    {
        var rows = await db.CatalogIdentityDecisions
            .AsNoTracking()
            .Where(d => d.OwnerUserId == ownerId && d.SubjectKind == subject && d.SubjectKey == key)
            .OrderByDescending(d => d.CreatedAtUtc)
            .ToListAsync(ct);

        var accepted = rows.FirstOrDefault(d =>
            d.Status is CatalogIdentityStatus.AutoAccepted or CatalogIdentityStatus.Chosen);
        var candidates = rows
            .Where(d => d.Status == CatalogIdentityStatus.Candidate)
            .OrderByDescending(d => d.Confidence)
            .Take(5)
            .Select(ToCandidateDto)
            .ToList();

        return new CatalogIdentityView(
            subject.ToString(),
            key,
            display,
            accepted is null ? null : ToCandidateDto(accepted),
            candidates);
    }

    private static CatalogIdentityView EmptyView(CatalogIdentitySubject subject, string key, string display) =>
        new(subject.ToString(), key, display, null, []);

    private static CatalogIdentityCandidateDto ToCandidateDto(CatalogIdentityDecision d) =>
        new(d.Id, d.TargetKind.ToString(), d.DiscogsId, d.TargetName, d.Method.ToString(),
            d.Confidence, d.Status.ToString(), null, null);

    private static CatalogIdentityDecisionDto ToDecisionDto(CatalogIdentityDecision d) =>
        new(d.Id, d.SubjectKind.ToString(), d.SubjectDisplay, d.TargetKind.ToString(), d.DiscogsId,
            d.TargetName, d.Method.ToString(), d.Confidence, d.Status.ToString(), d.RunId,
            d.CreatedAtUtc, d.DecidedAtUtc, d.DecidedBy);

    /// <summary>Read-only: the artist path never writes these rows, so they are not tracked.</summary>
    private async Task<List<SongMetadata>> LoadArtistSongsAsync(Guid ownerId, string artistName, CancellationToken ct)
    {
        var key = CatalogKeys.NameKey(artistName);
        var rows = await db.Songs
            .Where(s => s.OwnerUserId == ownerId && s.DeletedAtUtc == null)
            .Select(s => new SongKeyRow(s.Id, s.AlbumArtist, s.Artist, s.Album))
            .ToListAsync(ct);
        var ids = rows
            .Where(r => CatalogKeys.NameKey(r.AlbumArtist ?? r.Artist) == key
                || CatalogKeys.NameKey(r.Artist) == key)
            .Select(r => r.Id)
            .ToList();
        if (ids.Count == 0)
            return [];

        return await db.Songs
            .AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .OrderBy(s => s.Id)
            .ToListAsync(ct);
    }

    private Task<List<SongMetadata>> LoadAlbumSongsAsync(Guid ownerId, string albumArtist, string album, CancellationToken ct) =>
        LoadAlbumSongsByKeyAsync(ownerId, DiscogsMatchScoring.AlbumSubjectKey(albumArtist, album), includeDeleted: false, ct);

    /// <summary>
    /// Album keys fold diacritics and strip punctuation, which SQL cannot reproduce, so keys are
    /// computed in memory over a narrow untracked projection of the owner's songs; only the
    /// album's own rows are then loaded tracked.
    /// </summary>
    private async Task<List<SongMetadata>> LoadAlbumSongsByKeyAsync(
        Guid ownerId, string subjectKey, bool includeDeleted, CancellationToken ct)
    {
        var query = db.Songs
            .IgnoreQueryFilters()
            .Where(s => s.OwnerUserId == ownerId && s.Album != null);
        if (!includeDeleted)
            query = query.Where(s => s.DeletedAtUtc == null);

        var rows = await query
            .Select(s => new SongKeyRow(s.Id, s.AlbumArtist, s.Artist, s.Album))
            .ToListAsync(ct);
        var ids = rows
            .Where(r => DiscogsMatchScoring.AlbumSubjectKey(r.AlbumArtist ?? r.Artist ?? string.Empty, r.Album!) == subjectKey)
            .Select(r => r.Id)
            .ToList();
        if (ids.Count == 0)
            return [];

        return await db.Songs
            .IgnoreQueryFilters()
            .Where(s => ids.Contains(s.Id))
            .OrderBy(s => s.Id)
            .ToListAsync(ct);
    }

    private sealed record SongKeyRow(int Id, string? AlbumArtist, string? Artist, string? Album);

    /// <summary>
    /// Barcode and label+catno searches are fuzzy on Discogs, so a hit only earns its method's
    /// confidence floor when its own title and artist agree with the library album.
    /// </summary>
    private static double? CorroboratedSearchHitScore(
        DiscogsMatchScoring.LibraryAlbumHint hint,
        DiscogsSearchResult hit,
        bool requireBarcode = false,
        bool requireCatalogNumber = false)
    {
        var score = DiscogsMatchScoring.ScoreSearchHit(hint, hit, requireBarcode, requireCatalogNumber);
        if (score <= 0 || !DiscogsMatchScoring.SearchHitArtistMatches(hint, hit))
            return null;
        return score;
    }

    private static DiscogsMatchScoring.LibraryAlbumHint BuildAlbumHint(string albumArtist, string album, List<SongMetadata> songs) =>
        new(
            albumArtist,
            album,
            songs.Select(s => s.Year).FirstOrDefault(y => y is > 0),
            songs.Select(s => s.Upc).FirstOrDefault(CatalogKeys.IsUsableBarcode),
            songs.Select(s => s.CatalogNumber).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)),
            songs.Select(s => s.Label).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)),
            songs.Select(s => new DiscogsMatchScoring.LibraryTrack(s.Title ?? "", s.DurationSeconds)).ToList());

    /// <summary>
    /// MBIDs that identify the subject itself: the album artist's only when the album artist is the
    /// subject (not Various Artists on a compilation), and from the per-artist list only the entry at
    /// the subject's position (not a featured guest).
    /// </summary>
    private static IEnumerable<string> CollectArtistMbids(List<SongMetadata> songs, string subjectKey)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in songs)
        {
            if (!string.IsNullOrWhiteSpace(song.AlbumArtistMusicBrainzId)
                && CatalogKeys.NameKey(song.AlbumArtist) == subjectKey
                && seen.Add(song.AlbumArtistMusicBrainzId.Trim()))
            {
                yield return song.AlbumArtistMusicBrainzId.Trim();
            }

            if (AlignedArtistMbid(song, subjectKey) is { } mbid && seen.Add(mbid))
                yield return mbid;
        }
    }

    /// <summary>
    /// The subject's entry in <see cref="SongMetadata.ArtistMusicBrainzIds"/>, which is positionally
    /// aligned with <see cref="SongMetadata.Artists"/>. Lists of different lengths are not aligned.
    /// </summary>
    private static string? AlignedArtistMbid(SongMetadata song, string subjectKey)
    {
        var ids = MultiValue.Split(song.ArtistMusicBrainzIds);
        if (ids.Length == 0)
            return null;

        var artists = MultiValue.Split(song.Artists);
        if (artists.Length == 0)
            return ids.Length == 1 && CatalogKeys.NameKey(song.Artist) == subjectKey ? ids[0] : null;
        if (artists.Length != ids.Length)
            return null;

        var index = Array.FindIndex(artists, a => CatalogKeys.NameKey(a) == subjectKey);
        return index < 0 ? null : ids[index];
    }

    private async Task RecordConflictingRelationsAsync(
        Guid ownerId,
        string key,
        string display,
        List<IGrouping<long, (string Mbid, string Url, long DiscogsId)>> targets,
        int? runId,
        CancellationToken ct)
    {
        await ClearOpenCandidatesAsync(ownerId, CatalogIdentitySubject.LibraryArtist, key, ct);
        var totalMbids = targets.SelectMany(t => t.Select(h => h.Mbid)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var ranked = targets
            .Select(t => (DiscogsId: t.Key,
                Mbids: t.Select(h => h.Mbid).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Urls: t.Select(h => h.Url).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(t => t.Mbids.Count)
            .ThenBy(t => t.DiscogsId)
            .Take(5);

        foreach (var target in ranked)
        {
            db.CatalogIdentityDecisions.Add(new CatalogIdentityDecision
            {
                OwnerUserId = ownerId,
                SubjectKind = CatalogIdentitySubject.LibraryArtist,
                SubjectKey = key,
                SubjectDisplay = display,
                TargetKind = CatalogEntityKind.Artist,
                DiscogsId = target.DiscogsId,
                Method = CatalogIdentityMethod.MusicBrainzRelation,
                Confidence = Math.Round(0.4 + 0.4 * target.Mbids.Count / totalMbids, 2),
                Status = CatalogIdentityStatus.Candidate,
                EvidenceJson = Evidence(new
                {
                    discogsId = target.DiscogsId,
                    mbids = target.Mbids,
                    urls = target.Urls,
                    note = "conflicting-musicbrainz-relations",
                }),
                RunId = runId,
                CreatedAtUtc = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private static string StripSearchArtistTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;
        // Search results sometimes include "Artist (2)" already handled by CatalogKeys.
        return title.Trim();
    }

    private static string ParseAlbumDisplayKey(string subjectDisplay)
    {
        var (artist, album) = SplitAlbumDisplay(subjectDisplay);
        return DiscogsMatchScoring.AlbumSubjectKey(artist, album);
    }

    private static (string Artist, string Album) SplitAlbumDisplay(string subjectDisplay)
    {
        var text = subjectDisplay.Trim();
        var sep = text.IndexOf(" – ", StringComparison.Ordinal);
        if (sep < 0)
            sep = text.IndexOf(" - ", StringComparison.Ordinal);
        if (sep < 0)
            return (text, text);
        return (text[..sep].Trim(), text[(sep + 3)..].Trim());
    }

    private static string Evidence(object value) => JsonSerializer.Serialize(value, EvidenceJson);
}
