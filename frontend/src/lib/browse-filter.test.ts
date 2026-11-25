import { describe, expect, it } from 'vitest';
import type { ApiSong } from '#lib/api-client.ts';
import { applyBrowseFilter, browseFilterLabel, parseBrowseFilter } from './browse-filter.ts';

function params(query: string): URLSearchParams {
  return new URLSearchParams(query);
}

function song(partial: Partial<ApiSong> & Pick<ApiSong, 'id'>): ApiSong {
  return partial as ApiSong;
}

describe('parseBrowseFilter', () => {
  it('reads artist, year, unknown year, and label', () => {
    expect(parseBrowseFilter(params('artist=Aphex+Twin'))).toEqual({ artist: 'Aphex Twin' });
    expect(parseBrowseFilter(params('year=1992'))).toEqual({ year: 1992 });
    expect(parseBrowseFilter(params('year=unknown'))).toEqual({ yearUnknown: true });
    expect(parseBrowseFilter(params('label=Public+Possession'))).toEqual({
      label: 'Public Possession'
    });
  });

  it('prefers artist over label when both are present', () => {
    expect(parseBrowseFilter(params('artist=A&label=B'))).toEqual({ artist: 'A' });
  });
});

describe('applyBrowseFilter label', () => {
  const songs = [
    song({ id: 1, label: 'Public Possession' }),
    song({ id: 2, label: 'public possession; Other' }),
    song({ id: 3, label: 'Warp' }),
    song({ id: 4, label: null })
  ];

  it('matches any record-label credit case-insensitively', () => {
    expect(applyBrowseFilter(songs, { label: 'Public Possession' }).map((s) => s.id)).toEqual([
      1, 2
    ]);
    expect(applyBrowseFilter(songs, { label: 'other' }).map((s) => s.id)).toEqual([2]);
  });
});

describe('browseFilterLabel', () => {
  it('returns the label name', () => {
    expect(browseFilterLabel({ label: 'Warp' })).toBe('Warp');
  });
});
