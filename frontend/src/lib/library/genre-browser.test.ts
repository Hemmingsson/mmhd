import { describe, expect, it } from 'vitest';
import type { AlbumSummary } from '#lib/api-client.ts';
import { genreCount, genreIndex, genreMatches } from './genre-browser.ts';
import { countryName, profileParts } from './library-profile.ts';

describe('library genres', () => {
  it('counts each release once and preserves individual track styles', () => {
    const album = {
      genre: 'Ambient',
      songs: [{ genre: 'Ambient / Techno' }, { genre: 'Jungle' }]
    } as AlbumSummary;
    const { counts, releases } = genreIndex([album]);
    expect(genreCount([album, album])).toBe(counts.length);
    expect(counts).toEqual([
      ['Ambient', 1],
      ['Jungle', 1],
      ['Techno', 1]
    ]);
    expect(genreMatches(releases[0].tags, ['Techno', 'Jungle'], false)).toBe(true);
    expect(genreMatches(releases[0].tracks[0].styles, ['Techno', 'Jungle'], false)).toBe(false);
    expect(genreMatches(releases[0].tracks[0].styles, ['Techno', 'Jungle'], true)).toBe(true);
  });
});

describe('library profiles', () => {
  it('displays country names while preserving descriptive areas', () => {
    expect(countryName('SE')).toBe('Sweden');
    expect(countryName('Berlin')).toBe('Berlin');
  });
  it('links Discogs references without interpreting arbitrary markup', () => {
    expect(profileParts('Founded by [a=Example Artist] on [l123].')).toContainEqual({
      text: 'Example Artist',
      href: 'https://www.discogs.com/search/?type=artist&q=Example%20Artist'
    });
    expect(profileParts('[l123]')).toContainEqual({
      text: 'label 123',
      href: 'https://www.discogs.com/label/123'
    });
  });
});
