<script lang="ts">
  import { page } from '$app/state';
  import { ExternalLink } from '@lucide/svelte';
  import { fetchCatalogRelease } from '#lib/catalog/api.ts';
  import CatalogAvailabilityPanel from '#lib/catalog/components/CatalogAvailabilityPanel.svelte';
  import CatalogCredits from '#lib/catalog/components/CatalogCredits.svelte';
  import CatalogImageGallery from '#lib/catalog/components/CatalogImageGallery.svelte';
  import CatalogOwnedBadge from '#lib/catalog/components/CatalogOwnedBadge.svelte';
  import CatalogTracklist from '#lib/catalog/components/CatalogTracklist.svelte';
  import { formatCatalogFormats, catalogYearLabel } from '#lib/catalog/format.ts';
  import type { CatalogReleaseDetailDto } from '#lib/catalog/types.ts';
  import { Button } from '#lib/components/ui/button/index.ts';
  import { ScrollArea } from '#lib/components/ui/scroll-area/index.ts';
  import { Skeleton } from '#lib/components/ui/skeleton/index.ts';
  import PageToolbarV2 from '#lib/components/v2/PageToolbarV2.svelte';
  import { tabMemory } from '#lib/stores/tab-memory.svelte.ts';

  const id = $derived(Number(page.params.id));
  const detailPromise = $derived(
    Number.isFinite(id) ? fetchCatalogRelease(id) : Promise.reject(new Error('Invalid release id'))
  );

  function remember(detail: CatalogReleaseDetailDto) {
    tabMemory.setTitle(page.url, detail.title);
    return detail;
  }
</script>

<div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
  <ScrollArea class="min-h-0 flex-1">
    {#await detailPromise}
      <PageToolbarV2
        title="Release"
        meta="Loading…"
        back={{ label: 'Catalog', href: '/catalog?tab=releases' }}
        grouped
      />
      <div class="mx-auto flex w-full max-w-3xl flex-col gap-4 px-4 pb-10 md:px-7" aria-busy="true">
        <Skeleton class="aspect-square max-w-xs rounded-xl" />
        <Skeleton class="h-4 w-2/3" />
        <Skeleton class="h-24 w-full rounded-xl" />
      </div>
    {:then raw}
      {@const detail = remember(raw)}
      <PageToolbarV2
        title={detail.title}
        meta={[detail.artistDisplay, catalogYearLabel(detail.year), detail.country]
          .filter((x) => x && x !== '—')
          .join(' · ')}
        back={{ label: 'Catalog', href: '/catalog?tab=releases' }}
        grouped
      />
      <div class="mx-auto flex w-full max-w-3xl flex-col gap-8 px-4 pb-10 md:px-7">
        <header class="flex flex-col gap-4 sm:flex-row sm:items-start">
          <div class="min-w-0 flex-1 space-y-2">
            <div class="flex flex-wrap items-center gap-2">
              <CatalogOwnedBadge
                owned={detail.ownership.ownedSongs > 0 || detail.ownership.ownedReleases > 0}
              />
              {#if detail.master?.id != null}
                <a
                  href="/catalog/masters/{detail.master.id}"
                  class="text-primary text-subheadline hover:underline md:text-sm">Master</a
                >
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
            </div>

            {#if detail.styles.length > 0}
              <div class="flex flex-wrap gap-2" aria-label="Styles">
                {#each detail.styles as style (style)}
                  <a
                    href="/catalog?tab=releases&style={encodeURIComponent(style)}"
                    class="bg-secondary text-subheadline rounded-full px-3 py-1.5 md:text-sm"
                    >{style}</a
                  >
                {/each}
              </div>
            {/if}

            {#if detail.formats.length > 0}
              <p class="text-body md:text-sm">{formatCatalogFormats(detail.formats)}</p>
            {/if}

            {#if detail.labels.length > 0}
              <p class="text-body md:text-sm">
                <span class="text-muted-foreground">Label</span>
                {#each detail.labels as lab, i (lab.label.discogsId + ':' + i)}
                  {#if i > 0}<span>, </span>{/if}
                  {#if lab.label.id != null}
                    <a href="/catalog/labels/{lab.label.id}" class="text-primary hover:underline"
                      >{lab.label.name}</a
                    >
                  {:else}
                    {lab.label.name}
                  {/if}
                  {#if lab.catalogNumber}
                    <span class="text-muted-foreground"> — {lab.catalogNumber}</span>
                  {/if}
                {/each}
              </p>
            {/if}

            {#if detail.released}
              <p class="text-muted-foreground text-footnote md:text-xs">
                Released {detail.released}
                {#if detail.status}
                  · {detail.status}
                {/if}
              </p>
            {/if}
          </div>
        </header>

        <CatalogImageGallery images={detail.images} />

        <CatalogAvailabilityPanel releaseId={detail.id} />

        {#if detail.notes}
          <section aria-labelledby="release-notes">
            <h2 id="release-notes" class="text-title-2 mb-2 md:text-lg">Notes</h2>
            <p class="text-body whitespace-pre-wrap md:text-sm">{detail.notes}</p>
          </section>
        {/if}

        <section aria-labelledby="release-tracklist">
          <h2 id="release-tracklist" class="text-title-2 mb-2 md:text-lg">Tracklist</h2>
          <CatalogTracklist tracks={detail.tracklist} />
        </section>

        <CatalogCredits credits={detail.artists} title="Artists" />
        <CatalogCredits credits={detail.extraArtists} title="Credits" />

        {#if detail.companies.length > 0}
          <section>
            <h2 class="text-title-2 mb-2 md:text-lg">Companies</h2>
            <ul class="text-body flex flex-col gap-1 md:text-sm">
              {#each detail.companies as c, i (c.label.discogsId + ':' + i)}
                <li>
                  {#if c.entityTypeName}
                    <span class="text-muted-foreground">{c.entityTypeName}: </span>
                  {/if}
                  {#if c.label.id != null}
                    <a href="/catalog/labels/{c.label.id}" class="text-primary hover:underline"
                      >{c.label.name}</a
                    >
                  {:else}
                    {c.label.name}
                  {/if}
                </li>
              {/each}
            </ul>
          </section>
        {/if}

        {#if detail.series.length > 0}
          <section>
            <h2 class="text-title-2 mb-2 md:text-lg">Series</h2>
            <ul class="text-body flex flex-col gap-1 md:text-sm">
              {#each detail.series as s, i (s.label.discogsId + ':' + i)}
                <li>
                  {s.label.name}
                  {#if s.catalogNumber}
                    <span class="text-muted-foreground"> — {s.catalogNumber}</span>
                  {/if}
                </li>
              {/each}
            </ul>
          </section>
        {/if}

        {#if detail.identifiers.length > 0}
          <section>
            <h2 class="text-title-2 mb-2 md:text-lg">Identifiers</h2>
            <ul class="text-body flex flex-col gap-1 md:text-sm">
              {#each detail.identifiers as ident, i (ident.type + ':' + ident.value + ':' + i)}
                <li>
                  <span class="text-muted-foreground">{ident.type}:</span>
                  {ident.value}
                  {#if ident.description}
                    <span class="text-muted-foreground"> ({ident.description})</span>
                  {/if}
                </li>
              {/each}
            </ul>
          </section>
        {/if}

        {#if detail.externalIds.length > 0}
          <section>
            <h2 class="text-title-2 mb-2 md:text-lg">External ids</h2>
            <ul class="text-body flex flex-col gap-1 md:text-sm">
              {#each detail.externalIds as ext (ext.provider + ':' + ext.externalId)}
                <li>
                  <span class="text-muted-foreground">{ext.provider}:</span>
                  {ext.externalId}
                  <span class="text-muted-foreground text-footnote md:text-xs">
                    ({ext.source})</span
                  >
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
      </div>
    {:catch err}
      <PageToolbarV2
        title="Release"
        back={{ label: 'Catalog', href: '/catalog?tab=releases' }}
        grouped
      />
      <div class="mx-auto flex w-full max-w-3xl flex-col gap-3 px-4 py-10 md:px-7" role="alert">
        <p class="text-destructive-text text-subheadline md:text-sm">
          {err instanceof Error ? err.message : 'Failed to load release'}
        </p>
        <Button variant="gray" class="w-fit rounded-full" href="/catalog?tab=releases"
          >Back to catalog</Button
        >
      </div>
    {/await}
  </ScrollArea>
</div>
