/** Original provider terms, separate from the canonical genre vocabulary. */
export function trackTags(tags?: readonly string[] | null): string[] {
  const distinct = new Map<string, string>();
  for (const tag of tags ?? []) {
    const value = tag.trim();
    const key = value.toLowerCase();
    if (value && !distinct.has(key)) distinct.set(key, value);
  }
  return [...distinct.values()];
}
