// Discogs release dump vocabulary, extracted at
// https://github.com/hastefuI/discogs-dataset-genres-styles/tree/54456c947eaf4f6c408e4092b62b183420b4d198/dist
// The API embeds this same snapshot for provider enrichment and destination-file tags.
import taxonomy from './discogs-taxonomy.json';

function key(value: string): string {
  return value
    .trim()
    .toLowerCase()
    .replace(/\s+/g, ' ')
    .replace(/\s*([/,])\s*/g, '$1');
}

const vocabulary = new Map<string, string>();
for (const name of [...taxonomy.genres, ...taxonomy.styles]) vocabulary.set(key(name), name);
for (const [alias, canonical] of Object.entries(taxonomy.aliases)) {
  if (!vocabulary.has(key(canonical))) throw new Error(`Unknown Discogs term: ${canonical}`);
  vocabulary.set(key(alias), canonical);
}
const maxParts = Math.max(...[...vocabulary.keys()].map((name) => name.split(/([/,])/).length));

/** Whole vocabulary entries win over separators inside them (Funk / Soul, Cut-up/DJ). */
export function normalizeDiscogsGenres(raw?: string | null): string[] {
  if (!raw) return [];
  const result = new Set<string>();
  for (const field of raw.split(/[;|\r\n]+/)) {
    const parts = field.split(/([/,])/);
    for (let start = 0; start < parts.length; start += 2) {
      for (let end = Math.min(start + maxParts, parts.length); end > start; end -= 2) {
        const canonical = vocabulary.get(key(parts.slice(start, end).join('')));
        if (!canonical) continue;
        result.add(canonical);
        start = end - 1;
        break;
      }
    }
  }
  return [...result];
}
