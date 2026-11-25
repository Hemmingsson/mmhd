<script lang="ts">
  import { page } from '$app/state';
  import { onMount } from 'svelte';
  import { Disc3, Settings2 } from '@lucide/svelte';
  import {
    fetchCatalogArtists,
    fetchCatalogFacets,
    fetchCatalogLabels,
    fetchCatalogMasters,
    fetchCatalogReleases,
    fetchCatalogStyles,
    fetchCatalogTracks
  } from '#lib/catalog/api.ts';
  import CatalogArtistCard from '#lib/catalog/components/CatalogArtistCard.svelte';
  import CatalogFilters from '#lib/catalog/components/CatalogFilters.svelte';
  import type { CatalogFilterState } from '#lib/catalog/components/CatalogFilters.svelte';
  import CatalogLabelCard from '#lib/catalog/components/CatalogLabelCard.svelte';
  import CatalogMasterCard from '#lib/catalog/components/CatalogMasterCard.svelte';
  import CatalogPagination from '#lib/catalog/components/CatalogPagination.svelte';
  import CatalogReleaseCard from '#lib/catalog/components/CatalogReleaseCard.svelte';
  import type {
    CatalogArtistCardDto,
    CatalogFacetsDto,
    CatalogLabelCardDto,
    CatalogMasterCardDto,
    CatalogPage,
    CatalogReleaseCardDto,
    CatalogStyleDto,
    CatalogTrackRowDto
  } from '#lib/catalog/types.ts';
  import {
    CATALOG_TABS,
    parseCatalogTab,
    readCatalogFilters,
    writeCatalogSearchParams,
    type CatalogTab
  } from '#lib/catalog/url-state.ts';
  import { Button } from '#lib/components/ui/button/index.ts';
  import { EmptyState } from '#lib/components/ui/empty-state/index.ts';
  import { ScrollArea } from '#lib/components/ui/scroll-area/index.ts';
  import { SearchField } from '#lib/components/ui/search-field/index.ts';
  import { Skeleton } from '#lib/components/ui/skeleton/index.ts';
  import PageToolbarV2 from '#lib/components/v2/PageToolbarV2.svelte';
  import { replaceUrl } from '#lib/navigation/replace-url.ts';
  import { tabMemory } from '#lib/stores/tab-memory.svelte.ts';

  tabMemory.setTitle(page.url, 'Catalog');

  const tab = $derived(parseCatalogTab(page.url.searchParams.get('tab')));
  const q = $derived(page.url.searchParams.get('q') ?? '');
  const pageNum = $derived(Math.max(1, Number(page.url.searchParams.get('page') ?? '1') || 1));
  const pageSize = $derived(
    Math.min(200, Math.max(1, Number(page.url.searchParams.get('pageSize') ?? '50') || 50))
  );
  const filters = $derived(readCatalogFilters(page.url.searchParams));

  let facets = $state<CatalogFacetsDto | null>(null);
  let searchDraft = $state(page.url.searchParams.get('q') ?? '');

  type ListResult =
    | { tab: 'artists'; page: CatalogPage<CatalogArtistCardDto> }
    | { tab: 'labels'; page: CatalogPage<CatalogLabelCardDto> }
    | { tab: 'releases'; page: CatalogPage<CatalogReleaseCardDto> }
    | { tab: 'masters'; page: CatalogPage<CatalogMasterCardDto> }
    | { tab: 'styles'; page: CatalogPage<CatalogStyleDto> }
    | { tab: 'tracks'; page: CatalogPage<CatalogTrackRowDto> };

  const listPromise = $derived.by((): Promise<ListResult> => {
    const listQuery = { q: q || null, page: pageNum, pageSize };
    const releaseQuery = {
      ...listQuery,
      style: filters.style,
      format: filters.format,
      country: filters.country,
      yearFrom: filters.yearFrom,
      yearTo: filters.yearTo,
      owned: filters.owned,
      sort: filters.sort
    };
    switch (tab) {
      case 'artists':
        return fetchCatalogArtists(listQuery).then((p) => ({ tab, page: p }));
      case 'labels':
        return fetchCatalogLabels(listQuery).then((p) => ({ tab, page: p }));
      case 'releases':
        return fetchCatalogReleases(releaseQuery).then((p) => ({ tab, page: p }));
      case 'masters':
        return fetchCatalogMasters(releaseQuery).then((p) => ({ tab, page: p }));
      case 'styles':
        return fetchCatalogStyles(listQuery).then((p) => ({ tab, page: p }));
      default:
        return fetchCatalogTracks(listQuery).then((p) => ({ tab: 'tracks' as const, page: p }));
    }
  });

  onMount(() => {
    void fetchCatalogFacets()
      .then((f) => {
        facets = f;
      })
      .catch(() => {
        facets = null;
      });
  });

  async function syncUrl(opts: {
    tab?: CatalogTab;
    q?: string;
    page?: number;
    filters?: CatalogFilterState;
  }) {
    if (opts.q !== undefined) searchDraft = opts.q;
    const sp = writeCatalogSearchParams({
      tab: opts.tab ?? tab,
      q: opts.q ?? q,
      page: opts.page ?? pageNum,
      pageSize,
      filters: opts.filters ?? filters
    });
    const next = `/catalog${sp.toString() ? `?${sp}` : ''}`;
    await replaceUrl(next);
  }

  const showReleaseFilters = $derived(tab === 'releases' || tab === 'masters');
  const meta = $derived(`${tab}${q ? ` · “${q}”` : ''}`);
</script>

<div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
  <ScrollArea class="min-h-0 flex-1">
    <PageToolbarV2
      title="Catalog"
      {meta}
      metaFrom="lg"
      grouped
      tabs={CATALOG_TABS.map((t) => ({ id: t.id, label: t.label }))}
      activeTab={tab}
      onselectTab={(id) => void syncUrl({ tab: id as CatalogTab, page: 1 })}
    >
      {#snippet actions()}
        <Button
          variant="ghost"
          size="icon"
          class="size-11 rounded-full md:size-8"
          href="/catalog/admin"
          aria-label="Catalog admin"
        >
          <Settings2 class="size-5 md:size-4" />
        </Button>
      {/snippet}
      {#snippet search()}
        <SearchField
          bind:value={searchDraft}
          label="Search the catalog"
          onkeydown={(e) => {
            if (e.key === 'Enter') void syncUrl({ q: searchDraft, page: 1 });
          }}
          onblur={() => {
            if (searchDraft !== q) void syncUrl({ q: searchDraft, page: 1 });
          }}
          onclear={() => void syncUrl({ q: '', page: 1 })}
        />
      {/snippet}
      {#snippet filterRow()}
        {#if showReleaseFilters || facets}
          <CatalogFilters
            {facets}
            {filters}
            releaseFilters={showReleaseFilters}
            onchange={(next) => void syncUrl({ filters: next, page: 1 })}
          />
        {/if}
      {/snippet}
    </PageToolbarV2>

    <div class="flex flex-col gap-2 px-4 pb-8 md:px-7">
      {#await listPromise}
        <div class="flex flex-col gap-2 py-2" aria-busy="true" aria-label="Loading">
          {#each { length: 8 } as _, i (i)}
            <div class="flex gap-3 p-2">
              <Skeleton class="size-14 shrink-0 rounded-md" />
              <div class="flex flex-1 flex-col gap-2 self-center">
                <Skeleton class="h-4 w-2/3" />
                <Skeleton class="h-3 w-1/3" />
              </div>
            </div>
          {/each}
        </div>
      {:then result}
        {#if result.page.total === 0}
          <EmptyState
            icon={Disc3}
            title={q ? 'No matches' : 'Nothing in the catalog yet'}
            hint={q
              ? 'Try a different search, or clear it.'
              : 'Ingest from Discogs on the admin page, or run a backfill.'}
            action={q
              ? { label: 'Clear search', onclick: () => void syncUrl({ q: '', page: 1 }) }
              : undefined}
          />
        {:else if result.tab === 'artists'}
          <ul class="flex flex-col">
            {#each result.page.items as artist (artist.id)}
              <li>
                <CatalogArtistCard {artist} href="/catalog/artists/{artist.id}" />
              </li>
            {/each}
          </ul>
        {:else if result.tab === 'labels'}
          <ul class="flex flex-col">
            {#each result.page.items as label (label.id)}
              <li>
                <CatalogLabelCard {label} href="/catalog/labels/{label.id}" />
              </li>
            {/each}
          </ul>
        {:else if result.tab === 'releases'}
          <ul class="flex flex-col">
            {#each result.page.items as item (item.id)}
              <li>
                <CatalogReleaseCard {item} href="/catalog/releases/{item.id}" />
              </li>
            {/each}
          </ul>
        {:else if result.tab === 'masters'}
          <ul class="flex flex-col">
            {#each result.page.items as master (master.id)}
              <li>
                <CatalogMasterCard {master} href="/catalog/masters/{master.id}" />
              </li>
            {/each}
          </ul>
        {:else if result.tab === 'styles'}
          <ul class="flex flex-col">
            {#each result.page.items as style (style.name)}
              <li>
                <button
                  type="button"
                  class="hover:bg-accent focus-visible:ring-ring flex w-full items-center justify-between rounded-xl p-3 text-left focus-visible:ring-2 focus-visible:outline-hidden"
                  onclick={() =>
                    void syncUrl({
                      tab: 'releases',
                      page: 1,
                      filters: { ...filters, style: style.name }
                    })}
                >
                  <span class="text-headline md:text-sm md:font-semibold">{style.name}</span>
                  <span class="text-muted-foreground text-footnote md:text-xs"
                    >{style.releaseCount.toLocaleString()}</span
                  >
                </button>
              </li>
            {/each}
          </ul>
        {:else}
          <ul class="flex flex-col">
            {#each result.page.items as row (row.releaseId + ':' + row.sequence + ':' + row.title)}
              <li>
                <a
                  href="/catalog/releases/{row.releaseId}"
                  class="hover:bg-accent focus-visible:ring-ring flex flex-col gap-0.5 rounded-xl p-3 focus-visible:ring-2 focus-visible:outline-hidden"
                >
                  <span class="text-headline truncate md:text-sm md:font-semibold">
                    {row.position ? `${row.position}. ` : ''}{row.title}
                  </span>
                  <span class="text-muted-foreground text-subheadline truncate md:text-xs">
                    {[row.artistDisplay, row.releaseTitle, row.year, row.duration]
                      .filter(Boolean)
                      .join(' · ')}
                    {row.owned ? ' · Owned' : ''}
                  </span>
                </a>
              </li>
            {/each}
          </ul>
        {/if}

        <CatalogPagination
          page={pageNum}
          {pageSize}
          total={result.page.total}
          onpage={(p) => void syncUrl({ page: p })}
        />
      {:catch err}
        <div class="flex flex-col items-start gap-3 py-10" role="alert">
          <p class="text-destructive-text text-subheadline md:text-sm">
            {err instanceof Error ? err.message : 'Failed to load catalog'}
          </p>
          <Button variant="gray" class="rounded-full" onclick={() => location.reload()}
            >Retry</Button
          >
        </div>
      {/await}

      <p class="text-muted-foreground text-footnote pt-2 md:text-xs">
        <a href="/catalog/admin" class="text-primary hover:underline">Catalog admin</a>
        — ingest, identity review, backfill
      </p>
    </div>
  </ScrollArea>
</div>
