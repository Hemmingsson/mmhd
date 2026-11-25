import type { CatalogCreditDto } from './types.ts';

export type CreditGroup = {
  role: string;
  credits: CatalogCreditDto[];
};

/** Display name: ANV when present, else the catalog artist name. */
export function creditDisplayName(credit: CatalogCreditDto): string {
  const anv = credit.anv?.trim();
  if (anv) return anv;
  return credit.artist.name;
}

/**
 * Group credits by role (empty/missing → "Credit"), preserving first-seen role order
 * and credit order within each group.
 */
export function groupCreditsByRole(credits: readonly CatalogCreditDto[]): CreditGroup[] {
  const order: string[] = [];
  const map = new Map<string, CatalogCreditDto[]>();
  for (const credit of credits) {
    const role = credit.role?.trim() || 'Credit';
    let bucket = map.get(role);
    if (!bucket) {
      bucket = [];
      map.set(role, bucket);
      order.push(role);
    }
    bucket.push(credit);
  }
  return order.map((role) => ({ role, credits: map.get(role)! }));
}

/** Join credited names with Discogs join markers (" & ", ", ", " feat. "). */
export function joinCreditNames(credits: readonly CatalogCreditDto[]): string {
  if (credits.length === 0) return '';
  let out = creditDisplayName(credits[0]!);
  for (let i = 1; i < credits.length; i++) {
    const prev = credits[i - 1]!;
    const join = prev.join?.trim();
    out += join ? ` ${join} ` : ', ';
    out += creditDisplayName(credits[i]!);
  }
  return out;
}
