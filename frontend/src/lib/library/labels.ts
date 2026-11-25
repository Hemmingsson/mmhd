import type { AlbumSummary } from '#lib/api-client.ts';

export function recordLabels(value?: string | null): string[] {
  return [
    ...new Set(
      (value ?? '')
        .split(';')
        .map((name) => name.trim())
        .filter(Boolean)
    )
  ];
}

export function labelHref(name: string): string {
  return '/labels?name=' + encodeURIComponent(name);
}

export function groupByLabel(albums: readonly AlbumSummary[]) {
  const groups = new Map<string, { name: string; albums: AlbumSummary[] }>();
  for (const album of albums) {
    const names = [album.label, ...album.songs.map((song) => song.label)].flatMap(recordLabels);
    const seen = new Set<string>();
    for (const name of names) {
      const key = name.toLowerCase();
      if (seen.has(key)) continue;
      seen.add(key);
      const group = groups.get(key) ?? { name, albums: [] };
      group.albums.push(album);
      groups.set(key, group);
    }
  }
  return [...groups.values()].sort((a, b) => a.name.localeCompare(b.name));
}
