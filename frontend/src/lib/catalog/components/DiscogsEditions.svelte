<script lang="ts">
  import { SvelteMap } from 'svelte/reactivity';
  import { onMount, untrack } from 'svelte';
  import { labelEditions, resolveDiscogsRelease } from '#lib/catalog/api.ts';
  import CatalogAvailabilityPanel from '#lib/catalog/components/CatalogAvailabilityPanel.svelte';

  import CatalogReleaseCard from '#lib/catalog/components/CatalogReleaseCard.svelte';
  import type { CatalogDiscographyItemDto as LabelEdition } from '#lib/catalog/types.ts';
  let { id, cached = [] }: { id: number; cached?: LabelEdition[] } = $props();
  let entries = $state<LabelEdition[]>(untrack(() => cached));
  let page = 1;
  let pages = $state(1);
  let total = $state<number | null>(null);
  let busy = $state(true);
  let error = $state(false);
  const controller = new AbortController();
  let sort = $state('newest');
  let open = $state<number | null>(null);
  let detail = $state<Promise<{ id: number }> | null>(null);
  let limit = $state(50);
  const ordered = $derived(
    entries.toSorted((a, b) => {
      if (sort !== 'title') {
        if (!a.year && b.year) return 1;
        if (a.year && !b.year) return -1;
        const delta = (b.year ?? 0) - (a.year ?? 0);
        if (delta) return sort === 'newest' ? delta : -delta;
      }
      return (a.title ?? '').localeCompare(b.title ?? '') || a.discogsId - b.discogsId;
    })
  );
  async function load() {
    busy = true;
    error = false;
    try {
      while (page <= pages) {
        const data = await labelEditions(id, page, controller.signal);
        if (controller.signal.aborted) return;
        const merged = new SvelteMap((page === 1 ? [] : entries).map((e) => [e.discogsId, e]));
        for (const item of data.items) merged.set(item.discogsId, item);
        entries = [...merged.values()];
        total = data.pagination.items;
        pages = data.pagination.pages;
        page++;
      }
    } catch {
      if (!controller.signal.aborted) error = true;
    } finally {
      busy = false;
    }
  }
  onMount(() => {
    void load();
    return () => controller.abort();
  });
  function select(edition: LabelEdition) {
    if (open === edition.discogsId) {
      open = null;
      detail = null;
      return;
    }
    open = edition.discogsId;
    detail = resolveDiscogsRelease(edition.discogsId);
  }
</script>

<section class="my-6" aria-label="Discogs releases">
  <header class="flex flex-wrap items-baseline justify-between gap-2">
    <h2 class="text-title-2 font-bold">Discogs releases{total !== null ? ' · ' + total : ''}</h2>
    <label
      >Sort <select bind:value={sort}
        ><option value="newest">Newest first</option><option value="oldest">Oldest first</option
        ><option value="title">Title A–Z</option></select
      ></label
    >
  </header>
  {#if busy}<p role="status">
      Loading releases{total ? ' · ' + entries.length + ' of ' + total : '…'}
    </p>{/if}
  {#if error}
    <p role="alert">Couldn’t load the complete Discogs list.</p>
    <button onclick={load}>Retry loading Discogs releases</button>
  {/if}
  {#each ordered.slice(0, limit) as row (row.discogsId)}
    <div class="border-separator flex items-center justify-between gap-3 border-b py-2">
      <CatalogReleaseCard
        item={row}
        href={row.releaseId
          ? '/catalog/releases/' + row.releaseId
          : 'https://www.discogs.com/release/' + row.discogsId}
        class="min-w-0 flex-1"
      />
      <button
        class="text-primary shrink-0"
        aria-expanded={open === row.discogsId}
        aria-label={(open === row.discogsId
          ? 'Close download options for '
          : 'Download options for ') + row.title}
        onclick={() => select(row)}>{open === row.discogsId ? 'Close' : 'Download options'}</button
      >
    </div>
    {#if open === row.discogsId && detail}
      {#await detail}<p role="status">Checking release…</p>{:then release}
        {#key release.id}<CatalogAvailabilityPanel releaseId={release.id} />{/key}
      {:catch}<p>
          Couldn’t load this edition. <button
            onclick={() => (detail = resolveDiscogsRelease(row.discogsId))}>Retry</button
          >
        </p>{/await}
    {/if}
  {/each}
  {#if !busy && !error && !entries.length}<p>Discogs lists no releases for this label.</p>{/if}
  {#if entries.length > limit}<button class="text-primary my-3" onclick={() => (limit += 50)}
      >Show more releases</button
    >{/if}
</section>
