import { describe, expect, it } from 'vitest';
import type { AlbumSummary } from '#lib/api-client.ts';
import { groupByLabel, labelHref } from './labels.ts';

describe('label browsing', () => {
  it('lists a release once under each credited label across its tracks', () => {
    const album = {
      label: 'Public Possession',
      songs: [{ label: 'public possession; Other' }]
    } as AlbumSummary;
    const groups = groupByLabel([album]);
    expect(groups.map((group) => group.name)).toEqual(['Other', 'Public Possession']);
    expect(groups.map((group) => group.albums.length)).toEqual([1, 1]);
  });
  it('preserves punctuation when opening a label', () => {
    expect(new URL(labelHref('A&B / Co.'), 'https://example.org').searchParams.get('name')).toBe(
      'A&B / Co.'
    );
  });
});
