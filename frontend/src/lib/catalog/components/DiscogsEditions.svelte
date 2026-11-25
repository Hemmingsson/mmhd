<script lang="ts">
  import { SvelteMap, SvelteSet } from 'svelte/reactivity';
  import { onMount, untrack } from 'svelte';
  import { labelEditions, queueDiscogsReleases } from '#lib/catalog/api.ts';
  import { Download, LoaderCircle, Check } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import { acquireSummary } from '#lib/artist-discography.ts';
  import * as AlertDialog from '#lib/components/ui/alert-dialog/index.ts';

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
  let confirmAll = $state(false);
  const pending = new SvelteSet<number>();
  const finished = new SvelteSet<number>();
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
  const remaining = $derived(ordered.filter((row) => !finished.has(row.discogsId)));
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
  async function queue(rows: LabelEdition[]) {
    if (!rows.length || pending.size) return;
    const ids = rows.map((row) => row.discogsId);
    for (const id of ids) pending.add(id);
    const title = rows.length === 1 ? rows[0].title : `${rows.length} releases`;
    try {
      const result = await queueDiscogsReleases(ids);
      for (const item of result.results) {
        if (!item.error && !item.capped && (item.queued || item.alreadyOwned || item.alreadyQueued))
          finished.add(Number(item.key));
      }
      const detail = result.results.length === 1 ? result.results[0].error : null;
      if (detail && !result.queued) toast.error(title, { description: detail });
      else if (result.queued) toast.success(title, { description: acquireSummary(result) });
      else toast(title, { description: acquireSummary(result) });
    } catch (e) {
      toast.error(title, {
        description: e instanceof Error ? e.message : 'Could not queue this request.'
      });
    } finally {
      pending.clear();
    }
  }
</script>

<section class="my-6" aria-label="Discogs releases">
  <header class="flex flex-wrap items-baseline justify-between gap-2">
    <h2 class="text-title-2 font-bold">Discogs releases{total !== null ? ' · ' + total : ''}</h2>
    {#if remaining.length > 1}<button
        class="bg-primary/10 text-primary ml-auto inline-flex items-center gap-2 rounded-full px-3 py-1"
        disabled={busy || error || pending.size > 0}
        onclick={() => (confirmAll = true)}><Download class="size-4" />Get all missing</button
      >{/if}
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
        class="bg-primary text-primary-foreground grid size-10 shrink-0 place-items-center rounded-full disabled:opacity-70"
        aria-label={'Get ' + row.title}
        title={finished.has(row.discogsId) ? 'Already handled' : 'Get this release'}
        disabled={pending.size > 0 || finished.has(row.discogsId)}
        onclick={() => queue([row])}
      >
        {#if pending.has(row.discogsId)}<LoaderCircle
            class="size-5 animate-spin"
          />{:else if finished.has(row.discogsId)}<Check class="size-5" />{:else}<Download
            class="size-5"
          />{/if}
      </button>
    </div>
  {/each}
  {#if !busy && !error && !entries.length}<p>Discogs lists no releases for this label.</p>{/if}
  {#if entries.length > limit}<button class="text-primary my-3" onclick={() => (limit += 50)}
      >Show more releases</button
    >{/if}
</section>

<AlertDialog.Root bind:open={confirmAll}>
  <AlertDialog.Content
    ><AlertDialog.Header
      ><AlertDialog.Title>Get missing tracks from {remaining.length} releases?</AlertDialog.Title>
      <AlertDialog.Description
        >Queue missing tracks for download. Tracks already owned or on the way are skipped. The
        server’s request limit applies.</AlertDialog.Description
      ></AlertDialog.Header
    >
    <AlertDialog.Footer
      ><AlertDialog.Cancel>Cancel</AlertDialog.Cancel><AlertDialog.Action
        onclick={() => void queue(remaining)}>Get missing tracks</AlertDialog.Action
      ></AlertDialog.Footer
    ></AlertDialog.Content
  >
</AlertDialog.Root>
