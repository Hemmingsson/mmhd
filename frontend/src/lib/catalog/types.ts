/**
 * Wire DTOs for `/api/catalog`, mirrored field-for-field from
 * `MusicHoarder.Api/Catalog/CatalogContracts.cs` (camelCase JSON; enum-ish fields are strings).
 */

export type CatalogEntityKind = 'Artist' | 'Label' | 'Master' | 'Release';
export type CatalogIdentitySubject = 'LibraryArtist' | 'LibraryAlbum';
export type CatalogBackfillKind =
  | 'LibraryAlbums'
  | 'LibraryArtists'
  | 'CatalogResources'
  | 'Images';

export type CatalogPage<T> = {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
};

export type CatalogListQuery = {
  q?: string | null;
  page?: number;
  pageSize?: number;
};

/** Sort: "year" (default), "-year", "title", "added". */
export type CatalogReleaseQuery = {
  q?: string | null;
  artistId?: number | null;
  labelId?: number | null;
  masterId?: number | null;
  style?: string | null;
  format?: string | null;
  country?: string | null;
  yearFrom?: number | null;
  yearTo?: number | null;
  owned?: boolean | null;
  sort?: string | null;
  page?: number;
  pageSize?: number;
};

export type CatalogImageDto = {
  id: number;
  type: string;
  url: string | null;
  thumbnailUrl: string | null;
  width: number | null;
  height: number | null;
  local: boolean;
};

export type CatalogUrlDto = {
  url: string;
  kind: string;
};

export type CatalogExternalIdDto = {
  provider: string;
  externalId: string;
  source: string;
};

export type CatalogRefDto = {
  id: number | null;
  discogsId: number;
  name: string;
  fetchState: string;
};

export type CatalogArtistRelationDto = {
  kind: string;
  artist: CatalogRefDto;
  active: boolean | null;
};

export type CatalogOwnershipDto = {
  ownedSongs: number;
  ownedReleases: number;
};

export type CatalogArtistCardDto = {
  id: number;
  discogsId: number;
  name: string;
  fetchState: string;
  thumbnailUrl: string | null;
  releaseCount: number;
  ownedReleaseCount: number;
};

export type CatalogArtistDetailDto = {
  id: number;
  discogsId: number;
  name: string;
  realName: string | null;
  profile: string | null;
  fetchState: string;
  dataQuality: string | null;
  discogsUri: string | null;
  fetchedAtUtc: string | null;
  nameVariations: string[];
  aliases: CatalogArtistRelationDto[];
  members: CatalogArtistRelationDto[];
  groups: CatalogArtistRelationDto[];
  urls: CatalogUrlDto[];
  images: CatalogImageDto[];
  externalIds: CatalogExternalIdDto[];
  topStyles: CatalogStyleDto[];
  discography: CatalogDiscographyItemDto[];
  discographyTotal: number;
  ownership: CatalogOwnershipDto;
  libraryArtistNames: string[];
};

/** Kind: "master" or "release". Role: Discogs listing role. */
export type CatalogDiscographyItemDto = {
  kind: string;
  masterId: number | null;
  releaseId: number | null;
  discogsId: number;
  title: string;
  artistDisplay: string | null;
  year: number | null;
  role: string | null;
  format: string | null;
  label: string | null;
  catalogNumber: string | null;
  thumbnailUrl: string | null;
  owned: boolean;
};

export type CatalogLabelCardDto = {
  id: number;
  discogsId: number;
  name: string;
  fetchState: string;
  thumbnailUrl: string | null;
  releaseCount: number;
};

export type CatalogLabelDetailDto = {
  id: number;
  discogsId: number;
  name: string;
  profile: string | null;
  contactInfo: string | null;
  fetchState: string;
  dataQuality: string | null;
  discogsUri: string | null;
  parent: CatalogRefDto | null;
  sublabels: CatalogRefDto[];
  urls: CatalogUrlDto[];
  images: CatalogImageDto[];
  releases: CatalogDiscographyItemDto[];
  releaseTotal: number;
};

export type CatalogMasterCardDto = {
  id: number;
  discogsId: number;
  title: string;
  artistDisplay: string | null;
  year: number | null;
  fetchState: string;
  thumbnailUrl: string | null;
  styles: string[];
  versionCount: number;
  owned: boolean;
};

export type CatalogMasterDetailDto = {
  id: number;
  discogsId: number;
  title: string;
  artistDisplay: string | null;
  year: number | null;
  notes: string | null;
  fetchState: string;
  dataQuality: string | null;
  discogsUri: string | null;
  mainReleaseDiscogsId: number | null;
  mainReleaseId: number | null;
  styles: string[];
  artists: CatalogCreditDto[];
  images: CatalogImageDto[];
  urls: CatalogUrlDto[];
  tracklist: CatalogTrackDto[];
  versions: CatalogReleaseCardDto[];
  versionTotal: number;
  ownership: CatalogOwnershipDto;
};

export type CatalogReleaseCardDto = {
  id: number;
  discogsId: number;
  title: string;
  artistDisplay: string | null;
  year: number | null;
  country: string | null;
  format: string | null;
  label: string | null;
  catalogNumber: string | null;
  fetchState: string;
  thumbnailUrl: string | null;
  styles: string[];
  masterId: number | null;
  owned: boolean;
};

export type CatalogReleaseDetailDto = {
  id: number;
  discogsId: number;
  title: string;
  artistDisplay: string | null;
  year: number | null;
  released: string | null;
  country: string | null;
  status: string | null;
  notes: string | null;
  fetchState: string;
  dataQuality: string | null;
  discogsUri: string | null;
  fetchedAtUtc: string | null;
  master: CatalogRefDto | null;
  styles: string[];
  artists: CatalogCreditDto[];
  extraArtists: CatalogCreditDto[];
  labels: CatalogReleaseLabelDto[];
  companies: CatalogReleaseLabelDto[];
  series: CatalogReleaseLabelDto[];
  formats: CatalogFormatDto[];
  identifiers: CatalogIdentifierDto[];
  tracklist: CatalogTrackDto[];
  images: CatalogImageDto[];
  urls: CatalogUrlDto[];
  externalIds: CatalogExternalIdDto[];
  ownership: CatalogOwnershipDto;
};

export type CatalogCreditDto = {
  artist: CatalogRefDto;
  anv: string | null;
  join: string | null;
  role: string | null;
  tracks: string | null;
};

export type CatalogReleaseLabelDto = {
  label: CatalogRefDto;
  catalogNumber: string | null;
  entityTypeName: string | null;
};

export type CatalogFormatDto = {
  name: string;
  quantity: string | null;
  text: string | null;
  descriptions: string[];
};

export type CatalogIdentifierDto = {
  type: string;
  value: string;
  description: string | null;
};

/** Type: Discogs type_ — "track", "heading", "index". */
export type CatalogTrackDto = {
  sequence: number;
  position: string | null;
  type: string;
  title: string;
  duration: string | null;
  durationSeconds: number | null;
  discNumber: number | null;
  trackNumber: number | null;
  credits: CatalogCreditDto[];
  subTracks: CatalogTrackDto[];
  owned: boolean;
};

export type CatalogTrackRowDto = {
  releaseId: number;
  releaseTitle: string;
  artistDisplay: string | null;
  year: number | null;
  sequence: number;
  position: string | null;
  title: string;
  duration: string | null;
  owned: boolean;
};

export type CatalogStyleDto = {
  name: string;
  releaseCount: number;
};

export type CatalogFacetsDto = {
  styles: CatalogStyleDto[];
  formats: CatalogFacetValueDto[];
  countries: CatalogFacetValueDto[];
  minYear: number | null;
  maxYear: number | null;
};

export type CatalogFacetValueDto = {
  value: string;
  count: number;
};

export type CatalogProviderEditionDto = {
  provider: string;
  editionId: string;
  title: string;
  artist: string | null;
  trackCount: number | null;
  year: number | null;
  upc: string | null;
  confidence: number;
  url: string | null;
};

/** Kind: "purchase", "streaming", "info". */
export type CatalogPurchaseLinkDto = {
  name: string;
  url: string;
  kind: string;
};

/** Status: "owned", "available", "unavailable". */
export type CatalogAvailabilityDto = {
  releaseId: number;
  status: string;
  ownership: CatalogOwnershipDto;
  editions: CatalogProviderEditionDto[];
  purchaseLinks: CatalogPurchaseLinkDto[];
};

/** Status: "queued", "already-owned", "unavailable" (HTTP 409), "not-found" (404). */
export type CatalogAcquireResultDto = {
  status: string;
  queued: number;
  providerEditionId: string | null;
  message: string | null;
  purchaseLinks: CatalogPurchaseLinkDto[];
};

export type CatalogIdentityCandidateDto = {
  decisionId: number;
  targetKind: string;
  discogsId: number;
  targetName: string | null;
  method: string;
  confidence: number;
  status: string;
  catalogId: number | null;
  thumbnailUrl: string | null;
};

export type CatalogIdentityView = {
  subjectKind: string;
  subjectKey: string;
  subjectDisplay: string;
  accepted: CatalogIdentityCandidateDto | null;
  candidates: CatalogIdentityCandidateDto[];
};

export type CatalogIdentityDecisionDto = {
  id: number;
  subjectKind: string;
  subjectDisplay: string;
  targetKind: string;
  discogsId: number;
  targetName: string | null;
  method: string;
  confidence: number;
  status: string;
  runId: number | null;
  createdAtUtc: string;
  decidedAtUtc: string | null;
  decidedBy: string | null;
};

export type CatalogChooseRequest = {
  subject: CatalogIdentitySubject;
  subjectDisplay: string;
  target: CatalogEntityKind;
  discogsId: number;
};

export type CatalogBackfillStartRequest = {
  kind: CatalogBackfillKind;
  dryRun?: boolean;
};

export type CatalogBackfillRunDto = {
  id: number;
  kind: string;
  status: string;
  dryRun: boolean;
  cursor: string | null;
  processed: number;
  linked: number;
  candidates: number;
  unmatched: number;
  failed: number;
  songsUpdated: number;
  requestsUsed: number;
  lastError: string | null;
  startedBy: string | null;
  startedAtUtc: string;
  updatedAtUtc: string | null;
  finishedAtUtc: string | null;
};

export type CatalogIngestRequest = {
  kind: CatalogEntityKind;
  discogsId: number;
  withChildren?: boolean;
};

export type CatalogStatusDto = {
  enabled: boolean;
  authenticated: boolean;
  rateLimit: number | null;
  rateLimitRemaining: number | null;
  rateLimitObservedAtUtc: string | null;
  artists: number;
  labels: number;
  masters: number;
  releases: number;
  pendingImages: number;
};

/** `GET /api/catalog/library-artist?name=` body when found. */
export type CatalogLibraryArtistIdDto = {
  id: number;
};
