import { describe, expect, it } from 'vitest';
import { trackTags } from './track-tags.ts';

describe('original track tags', () => {
  it('keeps provider terms and punctuation while removing case-insensitive duplicates', () => {
    expect(trackTags([' UK garage ', 'uk GARAGE', 'Dance/Electronic', 'Funk, Soul', ''])).toEqual([
      'UK garage',
      'Dance/Electronic',
      'Funk, Soul'
    ]);
  });

  it('keeps terms outside the canonical genre vocabulary', () => {
    expect(trackTags(['Melancholic', 'late night', 'Deep House'])).toEqual([
      'Melancholic',
      'late night',
      'Deep House'
    ]);
  });

  it('allows older track responses without tags', () => {
    expect(trackTags(undefined)).toEqual([]);
    expect(trackTags(null)).toEqual([]);
    expect(trackTags([])).toEqual([]);
  });
});
