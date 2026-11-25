import { describe, expect, it } from 'vitest';
import { normalizeDiscogsGenres } from './discogs-genres.ts';
import taxonomy from './discogs-taxonomy.json';

describe('Discogs library tags', () => {
  it('recognizes each term from the pinned release vocabulary without splitting it', () => {
    for (const name of [...taxonomy.genres, ...taxonomy.styles]) {
      expect(normalizeDiscogsGenres(name)).toEqual([name]);
    }
  });

  it('splits mixed source delimiters, maps aliases, deduplicates and omits noise', () => {
    expect(
      normalizeDiscogsGenres(
        'Ambient / Experimental / Jungle / Lo-fi / Techno;ambient, dNb|anythinggoes;art.jpg'
      )
    ).toEqual(['Ambient', 'Experimental', 'Jungle', 'Lo-Fi', 'Techno', 'Drum n Bass']);
    expect(
      normalizeDiscogsGenres('RNB / Swing;Funk/Soul, Folk, World, & Country;Funk;Soul')
    ).toEqual(['RnB/Swing', 'Funk / Soul', 'Folk, World, & Country', 'Funk', 'Soul']);
    expect(normalizeDiscogsGenres('Baller; analog jam; House.png')).toEqual([]);
    expect(normalizeDiscogsGenres(null)).toEqual([]);
  });

  it('accepts reviewed source aliases and omits ambiguous model guesses', () => {
    expect(
      normalizeDiscogsGenres('Boombap / Detroit Techno; Avant-Garde, East Coast Hip Hop')
    ).toEqual(['Boom Bap', 'Techno', 'Avantgarde', 'Hip Hop']);
    expect(normalizeDiscogsGenres('Dance / Afro House / Chill Out')).toEqual([]);
  });
});
