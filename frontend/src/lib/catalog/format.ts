import { formatDuration } from '#lib/formatters.ts';
import type { CatalogFormatDto, CatalogTrackDto } from './types.ts';

/** Prefer the Discogs duration string; fall back to seconds. */
export function catalogTrackDuration(
  track: Pick<CatalogTrackDto, 'duration' | 'durationSeconds'>
): string {
  if (track.duration && track.duration.trim()) return track.duration.trim();
  return formatDuration(track.durationSeconds);
}

/** "2× Vinyl, LP, Album, Gatefold" — quantity × name, then descriptions / text. */
export function formatCatalogFormat(format: CatalogFormatDto): string {
  const qty = format.quantity && format.quantity !== '1' ? `${format.quantity}× ` : '';
  const parts = [format.name, ...format.descriptions];
  if (format.text) parts.push(format.text);
  return `${qty}${parts.filter(Boolean).join(', ')}`;
}

export function formatCatalogFormats(formats: readonly CatalogFormatDto[]): string {
  return formats.map(formatCatalogFormat).join(' · ');
}

export function formatConfidence(confidence: number): string {
  if (!Number.isFinite(confidence)) return '—';
  const pct = confidence * 100;
  if (pct >= 99.5) return '100%';
  if (pct >= 10) return `${Math.round(pct)}%`;
  if (pct > 0) return `${pct.toFixed(1)}%`;
  return '0%';
}

export function catalogYearLabel(year: number | null | undefined): string {
  return year != null && year > 0 ? String(year) : '—';
}

/** Flatten headings + tracks for a simple count (owned / total playable tracks). */
export function countCatalogTracks(tracks: readonly CatalogTrackDto[]): {
  total: number;
  owned: number;
} {
  let total = 0;
  let owned = 0;
  const walk = (list: readonly CatalogTrackDto[]) => {
    for (const t of list) {
      if (t.type === 'heading') {
        walk(t.subTracks);
        continue;
      }
      total += 1;
      if (t.owned) owned += 1;
      walk(t.subTracks);
    }
  };
  walk(tracks);
  return { total, owned };
}
