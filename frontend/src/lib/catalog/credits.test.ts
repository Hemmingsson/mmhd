import { describe, expect, it } from 'vitest';
import { creditDisplayName, groupCreditsByRole, joinCreditNames } from './credits.ts';
import type { CatalogCreditDto, CatalogRefDto } from './types.ts';

const artist = (name: string, id: number | null = 1): CatalogRefDto => ({
  id,
  discogsId: id ?? 0,
  name,
  fetchState: 'Fetched'
});

const credit = (partial: Partial<CatalogCreditDto> & { name: string }): CatalogCreditDto => ({
  artist: artist(partial.name),
  anv: partial.anv ?? null,
  join: partial.join ?? null,
  role: partial.role ?? null,
  tracks: partial.tracks ?? null
});

describe('creditDisplayName', () => {
  it('prefers ANV over the catalog name', () => {
    expect(creditDisplayName(credit({ name: 'John Doe', anv: 'J. Doe' }))).toBe('J. Doe');
    expect(creditDisplayName(credit({ name: 'John Doe' }))).toBe('John Doe');
  });
});

describe('groupCreditsByRole', () => {
  it('groups by role and keeps first-seen order', () => {
    const groups = groupCreditsByRole([
      credit({ name: 'A', role: 'Written-By' }),
      credit({ name: 'B', role: 'Producer' }),
      credit({ name: 'C', role: 'Written-By' }),
      credit({ name: 'D', role: null })
    ]);
    expect(groups.map((g) => g.role)).toEqual(['Written-By', 'Producer', 'Credit']);
    expect(groups[0]!.credits.map((c) => c.artist.name)).toEqual(['A', 'C']);
  });
});

describe('joinCreditNames', () => {
  it('uses Discogs join markers between names', () => {
    expect(
      joinCreditNames([
        credit({ name: 'Alice', join: '&' }),
        credit({ name: 'Bob', join: ',' }),
        credit({ name: 'Carol' })
      ])
    ).toBe('Alice & Bob , Carol');
  });

  it('falls back to commas when join is missing', () => {
    expect(joinCreditNames([credit({ name: 'A' }), credit({ name: 'B' })])).toBe('A, B');
  });
});
