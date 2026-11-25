import { API_PREFIX } from '#lib/library-requests.ts';

export interface ProfileLink {
  name: string;
  url: string;
}
export interface LibraryProfile {
  name: string;
  profile: string | null;
  country: string | null;
  realName: string | null;
  image: string | null;
  parent: ProfileLink | null;
  aliases: ProfileLink[];
  groups: ProfileLink[];
  variations: string[];
  links: string[];
  discogsId: number | null;
  discogsUrl: string | null;
}

export async function fetchLibraryProfile(kind: 'artist' | 'label', name: string) {
  const query = new URLSearchParams({ kind, name });
  const result = await fetch(`${API_PREFIX}/api/catalog/library-profile?${query}`);
  if (!result.ok) throw new Error('Profile lookup failed');
  if (result.status === 204) return null;
  return (await result.json()) as LibraryProfile;
}

export function countryName(value: string): string {
  return /^[a-z]{2}$/i.test(value)
    ? (new Intl.DisplayNames(['en'], { type: 'region' }).of(value.toUpperCase()) ?? value)
    : value;
}

// Discogs profiles contain bracketed cross-references. Render text and links without injecting HTML.
export function profileParts(text: string) {
  const parts: { text: string; href?: string }[] = [];
  let offset = 0;
  const value = text.replace(/\[\/?(?:b|i|u)\]/gi, '');
  for (const match of value.matchAll(/\[([alrm])(?:(\d+)|=([^\]]+))\]/gi)) {
    parts.push({ text: value.slice(offset, match.index) });
    const type = { a: 'artist', l: 'label', r: 'release', m: 'master' }[
      match[1].toLowerCase() as 'a' | 'l' | 'r' | 'm'
    ];
    const name = match[3] ?? `${type} ${match[2]}`;
    parts.push({
      text: name,
      href: match[2]
        ? `https://www.discogs.com/${type}/${match[2]}`
        : `https://www.discogs.com/search/?type=${type}&q=${encodeURIComponent(name)}`
    });
    offset = match.index + match[0].length;
  }
  parts.push({ text: value.slice(offset) });
  return parts;
}
