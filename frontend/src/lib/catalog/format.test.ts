import { describe, expect, it } from 'vitest';
import {
  catalogTrackDuration,
  catalogYearLabel,
  countCatalogTracks,
  formatCatalogFormat,
  formatCatalogFormats,
  formatConfidence
} from './format.ts';
import type { CatalogFormatDto, CatalogTrackDto } from './types.ts';

const track = (partial: Partial<CatalogTrackDto>): CatalogTrackDto => ({
  sequence: 1,
  position: '1',
  type: 'track',
  title: 'A',
  duration: null,
  durationSeconds: null,
  discNumber: null,
  trackNumber: null,
  credits: [],
  subTracks: [],
  owned: false,
  ...partial
});

describe('catalogTrackDuration', () => {
  it('prefers the Discogs duration string', () => {
    expect(catalogTrackDuration({ duration: '3:45', durationSeconds: 999 })).toBe('3:45');
  });

  it('falls back to seconds', () => {
    expect(catalogTrackDuration({ duration: null, durationSeconds: 125 })).toBe('2:05');
  });
});

describe('formatCatalogFormat', () => {
  it('joins quantity, name, descriptions and text', () => {
    const format: CatalogFormatDto = {
      name: 'Vinyl',
      quantity: '2',
      text: '180g',
      descriptions: ['LP', 'Album']
    };
    expect(formatCatalogFormat(format)).toBe('2× Vinyl, LP, Album, 180g');
  });

  it('omits a quantity of 1', () => {
    expect(
      formatCatalogFormat({ name: 'CD', quantity: '1', text: null, descriptions: ['Album'] })
    ).toBe('CD, Album');
  });
});

describe('formatCatalogFormats', () => {
  it('joins multiple formats', () => {
    expect(
      formatCatalogFormats([
        { name: 'CD', quantity: null, text: null, descriptions: [] },
        { name: 'Vinyl', quantity: null, text: null, descriptions: ['LP'] }
      ])
    ).toBe('CD · Vinyl, LP');
  });
});

describe('formatConfidence', () => {
  it('formats percentages', () => {
    expect(formatConfidence(0.87)).toBe('87%');
    expect(formatConfidence(1)).toBe('100%');
    expect(formatConfidence(0.05)).toBe('5.0%');
  });
});

describe('catalogYearLabel', () => {
  it('shows a year or an em dash', () => {
    expect(catalogYearLabel(1999)).toBe('1999');
    expect(catalogYearLabel(null)).toBe('—');
    expect(catalogYearLabel(0)).toBe('—');
  });
});

describe('countCatalogTracks', () => {
  it('skips headings and counts nested sub-tracks', () => {
    const list = [
      track({ type: 'heading', title: 'Side A', subTracks: [] }),
      track({
        title: 'One',
        owned: true,
        subTracks: [track({ title: 'One.a', owned: false })]
      }),
      track({ title: 'Two', owned: false })
    ];
    expect(countCatalogTracks(list)).toEqual({ total: 3, owned: 1 });
  });
});
