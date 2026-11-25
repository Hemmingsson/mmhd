<script lang="ts">
  import { page } from '$app/state';
  import { ExternalLink } from '@lucide/svelte';
  import { fetchCatalogLabel } from '#lib/catalog/api.ts';
  import CatalogImageGallery from '#lib/catalog/components/CatalogImageGallery.svelte';
  import CatalogReleaseCard from '#lib/catalog/components/CatalogReleaseCard.svelte';
  import type { CatalogDiscographyItemDto, CatalogLabelDetailDto } from '#lib/catalog/types.ts';
  import { Button } from '#lib/components/ui/button/index.ts';
  import { ScrollArea } from '#lib/components/ui/scroll-area/index.ts';
  import { Skeleton } from '#lib/components/ui/skeleton/index.ts';
  import PageToolbarV2 from '#lib/components/v2/PageToolbarV2.svelte';
  import { tabMemory } from '#lib/stores/tab-memory.svelte.ts';

  const id = $derived(Number(page.params.id));
  const detailPromise = $derived(
    Number.isFinite(id) ? fetchCatalogLabel(id) : Promise.reject(new Error('Invalid label id'))
  );

  function hrefFor(item: CatalogDiscographyItemDto): string {
    if (item.kind === 'master' && item.masterId != null) return `/catalog/masters/${item.masterId}`;
    if (item.releaseId != null) return `/catalog/releases/${item.releaseId}`;
    return '/catalog?tab=releases';
  }

  function remember(detail: CatalogLabelDetailDto) {
    tabMemory.setTitle(page.url, detail.name);
    return detail;
  }
</script>

<div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
  <ScrollArea class="min-h-0 flex-1">
    {#await detailPromise}
      <PageToolbarV2
        title="Label"
        meta="Loading…"
        back={{ label: 'Catalog', href: '/catalog?tab=labels' }}
        grouped
      />
      <div class="mx-auto flex w-full max-w-3xl flex-col gap-4 px-4 pb-10 md:px-7" aria-busy="true">
        <Skeleton class="aspect-square max-w-xs rounded-xl" />
        <Skeleton class="h-4 w-2/3" />
      </div>
    {:then raw}
      {@const detail = remember(raw)}
      <PageToolbarV2
        title={detail.name}
        meta={`${detail.releaseTotal} release${detail.releaseTotal === 1 ? '' : 's'}`}
        back={{ label: 'Catalog', href: '/catalog?tab=labels' }}
        grouped
      />
      <div class="mx-auto flex w-full max-w-3xl flex-col gap-8 px-4 pb-10 md:px-7">
        <CatalogImageGallery images={detail.images} />
        {#if detail.profile}
          <p class="text-body whitespace-pre-wrap md:text-sm">{detail.profile}</p>
        {/if}
        {#if detail.contactInfo}
          <p class="text-muted-foreground text-footnote whitespace-pre-wrap md:text-xs">
            {detail.contactInfo}
          </p>
        {/if}
        {#if detail.parent}
          <p class="text-body md:text-sm">
            <span class="text-muted-foreground">Parent</span>
            {#if detail.parent.id != null}
              <a href="/catalog/labels/{detail.parent.id}" class="text-primary hover:underline"
                >{detail.parent.name}</a
              >
            {:else}
              {detail.parent.name}
            {/if}
          </p>
        {/if}
        {#if detail.sublabels.length > 0}
          <section>
            <h2 class="text-title-2 mb-2 md:text-lg">Sublabels</h2>
            <ul class="flex flex-col gap-1">
              {#each detail.sublabels as sub (sub.discogsId)}
                <li>
                  {#if sub.id != null}
                    <a href="/catalog/labels/{sub.id}" class="text-primary hover:underline"
                      >{sub.name}</a
                    >
                  {:else}
                    {sub.name}
                  {/if}
                </li>
              {/each}
            </ul>
          </section>
        {/if}
        {#if detail.urls.length > 0}
          <section>
            <h2 class="text-title-2 mb-2 md:text-lg">Links</h2>
            <ul class="flex flex-col gap-1">
              {#each detail.urls as u (u.url)}
                <li>
                  <a
                    href={u.url}
                    target="_blank"
                    rel="noopener noreferrer"
                    class="text-primary text-subheadline inline-flex items-center gap-1 hover:underline md:text-sm"
                  >
                    {u.kind || u.url}
                    <ExternalLink class="size-3.5" aria-hidden="true" />
                  </a>
                </li>
              {/each}
            </ul>
          </section>
        {/if}
        {#if detail.discogsUri}
          <a
            href={detail.discogsUri}
            target="_blank"
            rel="noopener noreferrer"
            class="text-primary text-subheadline inline-flex items-center gap-1 hover:underline md:text-sm"
          >
            Discogs <ExternalLink class="size-3.5" aria-hidden="true" />
          </a>
        {/if}
        <section aria-labelledby="label-releases">
          <h2 id="label-releases" class="text-title-2 mb-2 md:text-lg">Releases</h2>
          <ul class="flex flex-col">
            {#each detail.releases as item (item.kind + ':' + (item.releaseId ?? item.masterId ?? item.discogsId))}
              <li>
                <CatalogReleaseCard {item} href={hrefFor(item)} />
              </li>
            {/each}
          </ul>
        </section>
      </div>
    {:catch err}
      <PageToolbarV2
        title="Label"
        back={{ label: 'Catalog', href: '/catalog?tab=labels' }}
        grouped
      />
      <div class="mx-auto flex w-full max-w-3xl flex-col gap-3 px-4 py-10 md:px-7" role="alert">
        <p class="text-destructive-text text-subheadline md:text-sm">
          {err instanceof Error ? err.message : 'Failed to load label'}
        </p>
        <Button variant="gray" class="w-fit rounded-full" href="/catalog?tab=labels"
          >Back to catalog</Button
        >
      </div>
    {/await}
  </ScrollArea>
</div>
