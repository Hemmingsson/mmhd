import { describe, expect, it } from 'vitest';
import { catalogImageSrc, parseDiscogsRef } from './api.ts';
import type { CatalogImageDto } from './types.ts';

describe('parseDiscogsRef', () => {
  it('parses Discogs URLs', () => {
    expect(parseDiscogsRef('https://www.discogs.com/release/12345-Some-Title')).toEqual({
      kind: 'Release',
      discogsId: 12345
    });
    expect(parseDiscogsRef('https://www.discogs.com/artist/99')).toEqual({
      kind: 'Artist',
      discogsId: 99
    });
    expect(parseDiscogsRef('https://www.discogs.com/master/7')).toEqual({
      kind: 'Master',
      discogsId: 7
    });
    expect(parseDiscogsRef('https://www.discogs.com/label/3')).toEqual({
      kind: 'Label',
      discogsId: 3
    });
  });

  it('parses a bare id with a fallback kind', () => {
    expect(parseDiscogsRef('4242')).toEqual({ kind: 'Release', discogsId: 4242 });
    expect(parseDiscogsRef('4242', 'Artist')).toEqual({ kind: 'Artist', discogsId: 4242 });
  });

  it('parses kind-prefixed ids', () => {
    expect(parseDiscogsRef('artist/55')).toEqual({ kind: 'Artist', discogsId: 55 });
    expect(parseDiscogsRef('release: 9')).toEqual({ kind: 'Release', discogsId: 9 });
  });

  it('returns null for junk', () => {
    expect(parseDiscogsRef('')).toBeNull();
    expect(parseDiscogsRef('not a ref')).toBeNull();
  });
});

describe('catalogImageSrc', () => {
  it('uses the local catalog image route when local', () => {
    const image: CatalogImageDto = {
      id: 12,
      type: 'primary',
      url: 'https://i.discogs.com/x.jpg',
      thumbnailUrl: 'https://i.discogs.com/t.jpg',
      width: 600,
      height: 600,
      local: true
    };
    expect(catalogImageSrc(image)).toBe('/api/mh/api/catalog/images/12');
  });

  it('uses remote URLs when not local', () => {
    const image: CatalogImageDto = {
      id: 1,
      type: 'primary',
      url: 'https://i.discogs.com/x.jpg',
      thumbnailUrl: 'https://i.discogs.com/t.jpg',
      width: null,
      height: null,
      local: false
    };
    expect(catalogImageSrc(image, true)).toBe('https://i.discogs.com/t.jpg');
    expect(catalogImageSrc(image)).toBe('https://i.discogs.com/x.jpg');
  });
});
