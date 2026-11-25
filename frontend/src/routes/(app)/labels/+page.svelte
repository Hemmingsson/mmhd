<script lang="ts">
  import { isAdmin } from '#lib/auth/capabilities.ts';
  import { fetchCatalogLabels } from '#lib/catalog/api.ts';
  import { API_PREFIX } from '#lib/library-requests.ts';
  import { page } from '$app/state';
  import { songsStore } from '#lib/stores/songs.svelte.ts';
  import { groupByLabel } from '#lib/library/labels.ts';
  import PageToolbarV2 from '#lib/components/v2/PageToolbarV2.svelte';
  import LabelPage from './LabelPage.svelte';

  const requested = $derived(page.url.searchParams.get('name')?.trim());
  const groups = $derived(groupByLabel(songsStore.albums));
  let portraits = $state<Record<string, string>>({});
  $effect(() => {
    if (requested || !isAdmin(page.data.user)) return;
    const controller = new AbortController();
    async function loadPortraits() {
      const images = new Map<string, string>();
      const seen = new Set<string>();
      for (let page = 1; ; page++) {
        const batch = await fetchCatalogLabels({ page, pageSize: 100 }, controller.signal);
        if (controller.signal.aborted) return;
        for (const label of batch.items) {
          const key = label.name.trim().toLowerCase();
          if (seen.has(key)) images.delete(key);
          else if (label.thumbnailUrl)
            images.set(
              key,
              label.thumbnailUrl.startsWith('/api/catalog/images/')
                ? API_PREFIX + label.thumbnailUrl
                : label.thumbnailUrl
            );
          seen.add(key);
        }
        if (!batch.items.length || page * batch.pageSize >= batch.total) break;
      }
      portraits = Object.fromEntries(images);
    }
    // Library tiles keep their initial when a catalog logo is unavailable.
    void loadPortraits().catch(() => {});
    return () => controller.abort();
  });
  $effect(() => {
    songsStore.revalidate();
    songsStore.startLive();
    return () => songsStore.stopLive();
  });
</script>

<div class="h-full overflow-y-auto">
  <PageToolbarV2 title={requested || 'Labels'} />
  <LabelPage
    {groups}
    {requested}
    loading={songsStore.isLoading}
    error={songsStore.error}
    retry={() => songsStore.revalidate()}
    {portraits}
  />
</div>
