// Batch adapter for the host metadata job. Keep its vocabulary identical to the library browser.
import { normalizeDiscogsGenres } from '../frontend/src/lib/library/discogs-genres.ts';

const values: unknown = await Bun.stdin.json();
if (!Array.isArray(values) || !values.every((value) => typeof value === 'string')) {
  throw new Error('Expected a JSON array of genre strings');
}
console.log(JSON.stringify(Object.fromEntries([...new Set<string>(values)].map((value) => [
  value, normalizeDiscogsGenres(value).join('; ') || null
]))));
