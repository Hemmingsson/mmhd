import type { AlbumSummary } from '#lib/api-client.ts';
import { normalizeDiscogsGenres } from './discogs-genres.ts';

export function genreCount(albums: readonly AlbumSummary[]) {
  const raw = new Set<string>();
  for (const album of albums) {
    if (album.genre) raw.add(album.genre);
    for (const song of album.songs) if (song.genre) raw.add(song.genre);
  }
  const styles = new Set<string>();
  for (const value of raw) {
    for (const style of normalizeDiscogsGenres(value)) styles.add(style);
  }
  return styles.size;
}

export function genreIndex(albums: readonly AlbumSummary[]) {
  const counts = new Map<string, number>();
  const releases = albums.map((album) => {
    const tags = new Set(normalizeDiscogsGenres(album.genre));
    const tracks = album.songs.map((song) => {
      const styles = normalizeDiscogsGenres(song.genre);
      for (const style of styles) tags.add(style);
      return { song, styles };
    });
    for (const tag of tags) counts.set(tag, (counts.get(tag) ?? 0) + 1);
    return { album, tags, tracks };
  });
  return { releases, counts: [...counts.entries()].sort(([a], [b]) => a.localeCompare(b)) };
}

export function genreMatches(
  tags: ReadonlySet<string> | readonly string[],
  selected: readonly string[],
  any: boolean
) {
  const contains = (tag: string) => ('has' in tags ? tags.has(tag) : tags.includes(tag));
  return !selected.length || (any ? selected.some(contains) : selected.every(contains));
}
