import type { CatalogFilterState } from './components/CatalogFilters.svelte';

export type CatalogTab = 'artists' | 'labels' | 'releases' | 'masters' | 'styles' | 'tracks';

export const CATALOG_TABS: readonly { id: CatalogTab; label: string }[] = [
  { id: 'artists', label: 'Artists' },
  { id: 'labels', label: 'Labels' },
  { id: 'releases', label: 'Releases' },
  { id: 'masters', label: 'Masters' },
  { id: 'styles', label: 'Styles' },
  { id: 'tracks', label: 'Tracks' }
];

const TAB_IDS = new Set<string>(CATALOG_TABS.map((t) => t.id));

export function parseCatalogTab(raw: string | null): CatalogTab {
  return raw && TAB_IDS.has(raw) ? (raw as CatalogTab) : 'artists';
}

export function defaultCatalogFilters(): CatalogFilterState {
  return {
    style: null,
    format: null,
    country: null,
    yearFrom: null,
    yearTo: null,
    owned: null,
    sort: 'year'
  };
}

export function readCatalogFilters(params: Pick<URLSearchParams, 'get'>): CatalogFilterState {
  const ownedRaw = params.get('owned');
  let owned: boolean | null = null;
  if (ownedRaw === 'true') owned = true;
  else if (ownedRaw === 'false') owned = false;

  const yearFrom = params.get('yearFrom');
  const yearTo = params.get('yearTo');

  return {
    style: params.get('style'),
    format: params.get('format'),
    country: params.get('country'),
    yearFrom: yearFrom ? Number(yearFrom) : null,
    yearTo: yearTo ? Number(yearTo) : null,
    owned,
    sort: params.get('sort') || 'year'
  };
}

export function writeCatalogSearchParams(opts: {
  tab: CatalogTab;
  q: string;
  page: number;
  pageSize: number;
  filters: CatalogFilterState;
}): URLSearchParams {
  const sp = new URLSearchParams();
  if (opts.tab !== 'artists') sp.set('tab', opts.tab);
  if (opts.q.trim()) sp.set('q', opts.q.trim());
  if (opts.page > 1) sp.set('page', String(opts.page));
  if (opts.pageSize !== 50) sp.set('pageSize', String(opts.pageSize));

  const f = opts.filters;
  if (f.style) sp.set('style', f.style);
  if (f.format) sp.set('format', f.format);
  if (f.country) sp.set('country', f.country);
  if (f.yearFrom != null) sp.set('yearFrom', String(f.yearFrom));
  if (f.yearTo != null) sp.set('yearTo', String(f.yearTo));
  if (f.owned != null) sp.set('owned', String(f.owned));
  if (f.sort && f.sort !== 'year') sp.set('sort', f.sort);
  return sp;
}
