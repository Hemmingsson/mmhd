/**
 * Typed client for `/api/catalog` and `/api/catalog/admin`.
 * Uses the same-origin proxy prefix and {@link ApiError} from the shared API client.
 */
import { ApiError } from '#lib/api-client.ts';
import { API_PREFIX } from '#lib/library-requests.ts';
import type {
  CatalogAcquireResultDto,
  CatalogArtistCardDto,
  CatalogArtistDetailDto,
  CatalogAvailabilityDto,
  CatalogBackfillRunDto,
  CatalogBackfillStartRequest,
  CatalogChooseRequest,
  CatalogEntityKind,
  CatalogFacetsDto,
  CatalogIdentityDecisionDto,
  CatalogIdentityView,
  CatalogImageDto,
  CatalogIngestRequest,
  CatalogLabelCardDto,
  CatalogLabelDetailDto,
  CatalogLibraryArtistIdDto,
  CatalogListQuery,
  CatalogMasterCardDto,
  CatalogMasterDetailDto,
  CatalogPage,
  CatalogReleaseCardDto,
  CatalogReleaseDetailDto,
  CatalogReleaseQuery,
  CatalogStatusDto,
  CatalogStyleDto,
  CatalogTrackRowDto
} from './types.ts';

const BASE = '/api/catalog';
const ADMIN = `${BASE}/admin`;

function qs(params: Record<string, string | number | boolean | null | undefined>): string {
  const sp = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value == null || value === '') continue;
    sp.set(key, String(value));
  }
  const s = sp.toString();
  return s ? `?${s}` : '';
}

async function throwIfFailed(path: string, response: Response): Promise<void> {
  if (response.ok) return;
  let detail = '';
  let code: string | null = null;
  try {
    const body = (await response.json()) as Record<string, unknown>;
    if (typeof body.error === 'string') code = body.error;
    if (body.error === 'demo_read_only') {
      throw new ApiError(
        'This action is disabled — the demo account is read-only.',
        'demo_read_only',
        response.status
      );
    }
    if (body.error === 'member_write_denied' || body.error === 'friend_read_only') {
      throw new ApiError(
        'This action is only available to an administrator.',
        body.error,
        response.status
      );
    }
    if (body.error === 'capability_required') {
      throw new ApiError(
        'Your account does not have access to that. Ask an administrator to enable it.',
        'capability_required',
        response.status
      );
    }
    if (body.error === 'admin_required' || body.error === 'owner_required') {
      throw new ApiError('Only an administrator can do that.', body.error, response.status);
    }
    detail =
      (body.message as string) ??
      (typeof body.error === 'string' ? body.error.replaceAll('_', ' ') : JSON.stringify(body));
  } catch (err) {
    if (err instanceof ApiError) throw err;
  }
  throw new ApiError(
    detail || `Request failed for ${path}: ${response.status}`,
    code,
    response.status
  );
}

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  const method = (init?.method ?? 'GET').toUpperCase();
  const isBodyMethod = method !== 'GET' && method !== 'HEAD';
  const response = await fetch(`${API_PREFIX}${path}`, {
    ...init,
    headers: isBodyMethod
      ? { 'content-type': 'application/json', ...(init?.headers ?? {}) }
      : init?.headers,
    cache: 'no-store'
  });
  await throwIfFailed(path, response);
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

function listParams(query: CatalogListQuery = {}): string {
  return qs({ q: query.q, page: query.page, pageSize: query.pageSize });
}

function releaseParams(query: CatalogReleaseQuery = {}): string {
  return qs({
    q: query.q,
    artistId: query.artistId,
    labelId: query.labelId,
    masterId: query.masterId,
    style: query.style,
    format: query.format,
    country: query.country,
    yearFrom: query.yearFrom,
    yearTo: query.yearTo,
    owned: query.owned,
    sort: query.sort,
    page: query.page,
    pageSize: query.pageSize
  });
}

/** Local catalog image URL when `local` is true; otherwise remote Discogs URL. */
export function catalogImageSrc(image: CatalogImageDto, preferThumb = false): string | null {
  if (image.local) return `${API_PREFIX}${BASE}/images/${image.id}`;
  if (preferThumb) return image.thumbnailUrl ?? image.url;
  return image.url ?? image.thumbnailUrl;
}

export function fetchCatalogArtists(
  query: CatalogListQuery = {}
): Promise<CatalogPage<CatalogArtistCardDto>> {
  return requestJson(`${BASE}/artists${listParams(query)}`);
}

export function fetchCatalogArtist(id: number): Promise<CatalogArtistDetailDto> {
  return requestJson(`${BASE}/artists/${id}`);
}

export function fetchCatalogLabels(
  query: CatalogListQuery = {}
): Promise<CatalogPage<CatalogLabelCardDto>> {
  return requestJson(`${BASE}/labels${listParams(query)}`);
}

export function fetchCatalogLabel(id: number): Promise<CatalogLabelDetailDto> {
  return requestJson(`${BASE}/labels/${id}`);
}

export function fetchCatalogMasters(
  query: CatalogReleaseQuery = {}
): Promise<CatalogPage<CatalogMasterCardDto>> {
  return requestJson(`${BASE}/masters${releaseParams(query)}`);
}

export function fetchCatalogMaster(id: number): Promise<CatalogMasterDetailDto> {
  return requestJson(`${BASE}/masters/${id}`);
}

export function fetchCatalogReleases(
  query: CatalogReleaseQuery = {}
): Promise<CatalogPage<CatalogReleaseCardDto>> {
  return requestJson(`${BASE}/releases${releaseParams(query)}`);
}

export function fetchCatalogRelease(id: number): Promise<CatalogReleaseDetailDto> {
  return requestJson(`${BASE}/releases/${id}`);
}

export function fetchCatalogTracks(
  query: CatalogListQuery = {}
): Promise<CatalogPage<CatalogTrackRowDto>> {
  return requestJson(`${BASE}/tracks${listParams(query)}`);
}

export function fetchCatalogStyles(
  query: CatalogListQuery = {}
): Promise<CatalogPage<CatalogStyleDto>> {
  return requestJson(`${BASE}/styles${listParams(query)}`);
}

export function fetchCatalogFacets(): Promise<CatalogFacetsDto> {
  return requestJson(`${BASE}/facets`);
}

/** Resolves a library artist name to a catalog artist id, or `null` on 404. */
export async function fetchLibraryArtistId(name: string): Promise<number | null> {
  const path = `${BASE}/library-artist${qs({ name })}`;
  const response = await fetch(`${API_PREFIX}${path}`, { cache: 'no-store' });
  if (response.status === 404) return null;
  await throwIfFailed(path, response);
  const body = (await response.json()) as CatalogLibraryArtistIdDto;
  return body.id;
}

export function fetchCatalogAvailability(releaseId: number): Promise<CatalogAvailabilityDto> {
  return requestJson(`${BASE}/releases/${releaseId}/availability`);
}

/**
 * Queue a release for acquisition. HTTP 409 returns the body with status `"unavailable"`
 * (purchase links, nothing queued) instead of throwing.
 */
export async function acquireCatalogRelease(
  releaseId: number,
  body: { providerEditionId?: string | null } = {}
): Promise<CatalogAcquireResultDto> {
  const path = `${BASE}/releases/${releaseId}/acquire`;
  const response = await fetch(`${API_PREFIX}${path}`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
    cache: 'no-store'
  });
  if (response.status === 409) {
    return (await response.json()) as CatalogAcquireResultDto;
  }
  await throwIfFailed(path, response);
  return (await response.json()) as CatalogAcquireResultDto;
}

export function fetchCatalogStatus(): Promise<CatalogStatusDto> {
  return requestJson(`${ADMIN}/status`);
}

export function ingestCatalogEntity(request: CatalogIngestRequest): Promise<unknown> {
  return requestJson(`${ADMIN}/ingest`, {
    method: 'POST',
    body: JSON.stringify({
      kind: request.kind,
      discogsId: request.discogsId,
      withChildren: request.withChildren ?? true
    })
  });
}

export function fetchIdentityReview(): Promise<CatalogIdentityDecisionDto[]> {
  return requestJson(`${ADMIN}/identity/review`);
}

export function fetchIdentityArtist(name: string): Promise<CatalogIdentityView> {
  return requestJson(`${ADMIN}/identity/artist${qs({ name })}`);
}

export function resolveIdentityArtist(body: {
  name: string;
  dryRun?: boolean;
}): Promise<CatalogIdentityView> {
  return requestJson(`${ADMIN}/identity/artist/resolve`, {
    method: 'POST',
    body: JSON.stringify({ name: body.name, dryRun: body.dryRun ?? true })
  });
}

export function resolveIdentityAlbum(body: {
  albumArtist: string;
  album: string;
  dryRun?: boolean;
}): Promise<CatalogIdentityView> {
  return requestJson(`${ADMIN}/identity/album/resolve`, {
    method: 'POST',
    body: JSON.stringify({
      albumArtist: body.albumArtist,
      album: body.album,
      dryRun: body.dryRun ?? true
    })
  });
}

export function chooseIdentity(request: CatalogChooseRequest): Promise<CatalogIdentityView> {
  return requestJson(`${ADMIN}/identity/choose`, {
    method: 'POST',
    body: JSON.stringify(request)
  });
}

export function rejectIdentity(id: number): Promise<unknown> {
  return requestJson(`${ADMIN}/identity/${id}/reject`, { method: 'POST' });
}

export function startBackfill(
  request: CatalogBackfillStartRequest
): Promise<CatalogBackfillRunDto> {
  return requestJson(`${ADMIN}/backfill`, {
    method: 'POST',
    body: JSON.stringify({ kind: request.kind, dryRun: request.dryRun ?? true })
  });
}

export function runBackfillBatch(id: number): Promise<CatalogBackfillRunDto> {
  return requestJson(`${ADMIN}/backfill/${id}/batch`, { method: 'POST' });
}

export function fetchBackfillRuns(): Promise<CatalogBackfillRunDto[]> {
  return requestJson(`${ADMIN}/backfill/runs`);
}

export function rollbackBackfill(id: number): Promise<CatalogBackfillRunDto> {
  return requestJson(`${ADMIN}/backfill/${id}/rollback`, { method: 'POST' });
}

export type ParsedDiscogsRef = { kind: CatalogEntityKind; discogsId: number };

/** Parse a Discogs URL or bare numeric id (+ optional kind) for ingest. */
export function parseDiscogsRef(
  input: string,
  fallbackKind: CatalogEntityKind = 'Release'
): ParsedDiscogsRef | null {
  const trimmed = input.trim();
  if (!trimmed) return null;

  const urlMatch = /discogs\.com\/(?:[^/]+\/)?(artist|label|master|release)\/(\d+)/i.exec(trimmed);
  if (urlMatch) {
    const kindMap: Record<string, CatalogEntityKind> = {
      artist: 'Artist',
      label: 'Label',
      master: 'Master',
      release: 'Release'
    };
    const kind = kindMap[urlMatch[1]!.toLowerCase()];
    const discogsId = Number(urlMatch[2]);
    if (kind && Number.isFinite(discogsId)) return { kind, discogsId };
  }

  const bare = /^(\d+)$/.exec(trimmed);
  if (bare) {
    const discogsId = Number(bare[1]);
    if (Number.isFinite(discogsId)) return { kind: fallbackKind, discogsId };
  }

  const prefixed = /^(artist|label|master|release)\s*[/:#]?\s*(\d+)$/i.exec(trimmed);
  if (prefixed) {
    const kindMap: Record<string, CatalogEntityKind> = {
      artist: 'Artist',
      label: 'Label',
      master: 'Master',
      release: 'Release'
    };
    const kind = kindMap[prefixed[1]!.toLowerCase()];
    const discogsId = Number(prefixed[2]);
    if (kind && Number.isFinite(discogsId)) return { kind, discogsId };
  }

  return null;
}
