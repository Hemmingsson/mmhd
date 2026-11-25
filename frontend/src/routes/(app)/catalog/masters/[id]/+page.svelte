<script lang="ts">
  import { page } from '$app/state';
  import { ExternalLink } from '@lucide/svelte';
  import { fetchCatalogMaster } from '#lib/catalog/api.ts';
  import CatalogCredits from '#lib/catalog/components/CatalogCredits.svelte';
  import CatalogImageGallery from '#lib/catalog/components/CatalogImageGallery.svelte';
  import CatalogOwnedBadge from '#lib/catalog/components/CatalogOwnedBadge.svelte';
  import CatalogReleaseCard from '#lib/catalog/components/CatalogReleaseCard.svelte';
  import CatalogTracklist from '#lib/catalog/components/CatalogTracklist.svelte';
  import { catalogYearLabel } from '#lib/catalog/format.ts';
  import type { CatalogMasterDetailDto, CatalogReleaseCardDto } from '#lib/catalog/types.ts';
  import { Button } from '#lib/components/ui/button/index.ts';
  import { Input } from '#lib/components/ui/input/index.ts';
  import { ScrollArea } from '#lib/components/ui/scroll-area/index.ts';
  import { Skeleton } from '#lib/components/ui/skeleton/index.ts';
  import FilterChip from '#lib/components/v2/FilterChip.svelte';
  import PageToolbarV2 from '#lib/components/v2/PageToolbarV2.svelte';
  import { tabMemory } from '#lib/stores/tab-memory.svelte.ts';

  const id = $derived(Number(page.params.id));
  const detailPromise = $derived(
    Number.isFinite(id) ? fetchCatalogMaster(id) : Promise.reject(new Error('Invalid master id'))
  );

  let countryFilter = $state<string | null>(null);
  let formatFilter = $state<string | null>(null);
  let ownedOnly = $state(false);
  let versionQuery = $state('');

  function remember(detail: CatalogMasterDetailDto) {
    tabMemory.setTitle(page.url, detail.title);
    return detail;
  }

  function countriesOf(detail: CatalogMasterDetailDto): string[] {
    return [
      ...new Set(detail.versions.map((v) => v.country).filter((c): c is string => Boolean(c)))
    ];
  }

  function formatsOf(detail: CatalogMasterDetailDto): string[] {
    return [
      ...new Set(detail.versions.map((v) => v.format).filter((f): f is string => Boolean(f)))
    ];
  }

  function versionsOf(detail: CatalogMasterDetailDto): CatalogReleaseCardDto[] {
    const q = versionQuery.trim().toLowerCase();
    return detail.versions.filter((v) => {
      if (ownedOnly && !v.owned) return false;
      if (countryFilter && v.country !== countryFilter) return false;
      if (formatFilter && v.format !== formatFilter) return false;
      if (!q) return true;
      return [v.title, v.artistDisplay, v.label, v.catalogNumber, v.country, v.format]
        .filter(Boolean)
        .some((s) => s!.toLowerCase().includes(q));
    });
  }
</script>

<div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
  <ScrollArea class="min-h-0 flex-1">
    {#await detailPromise}
      <PageToolbarV2
        title="Master"
        meta="Loading…"
        back={{ label: 'Catalog', href: '/catalog?tab=masters' }}
        grouped
      />
      <div class="mx-auto flex w-full max-w-3xl flex-col gap-4 px-4 pb-10 md:px-7" aria-busy="true">
        <Skeleton class="aspect-square max-w-xs rounded-xl" />
        <Skeleton class="h-4 w-2/3" />
      </div>
    {:then raw}
      {@const detail = remember(raw)}
      {@const countries = countriesOf(detail)}
      {@const formats = formatsOf(detail)}
      {@const versions = versionsOf(detail)}
      <PageToolbarV2
        title={detail.title}
        meta={[
          detail.artistDisplay,
          catalogYearLabel(detail.year),
          `${detail.versionTotal} versions`
        ]
          .filter((x) => x && x !== '—')
          .join(' · ')}
        back={{ label: 'Catalog', href: '/catalog?tab=masters' }}
        grouped
      />
      <div class="mx-auto flex w-full max-w-3xl flex-col gap-8 px-4 pb-10 md:px-7">
        <div class="flex flex-wrap items-center gap-2">
          <CatalogOwnedBadge
            owned={detail.ownership.ownedReleases > 0 || detail.ownership.ownedSongs > 0}
          />
          {#if detail.mainReleaseId != null}
            <a
              href="/catalog/releases/{detail.mainReleaseId}"
              class="text-primary text-subheadline hover:underline md:text-sm">Main release</a
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

        <CatalogImageGallery images={detail.images} />

        {#if detail.styles.length > 0}
          <div class="flex flex-wrap gap-2">
            {#each detail.styles as style (style)}
              <a
                href="/catalog?tab=releases&style={encodeURIComponent(style)}"
                class="bg-secondary text-subheadline rounded-full px-3 py-1.5 md:text-sm">{style}</a
              >
            {/each}
          </div>
        {/if}

        {#if detail.notes}
          <p class="text-body whitespace-pre-wrap md:text-sm">{detail.notes}</p>
        {/if}

        <CatalogCredits credits={detail.artists} title="Artists" />
        <CatalogTracklist tracks={detail.tracklist} />

        <section aria-labelledby="master-versions">
          <h2 id="master-versions" class="text-title-2 mb-3 md:text-lg">Versions</h2>
          <div class="mb-3 flex flex-col gap-3">
            <Input
              type="search"
              inputmode="search"
              enterkeyhint="search"
              autocapitalize="off"
              autocorrect="off"
              placeholder="Filter versions"
              aria-label="Filter versions"
              bind:value={versionQuery}
              class="h-11 md:h-8"
            />
            <div class="flex flex-wrap gap-2">
              <FilterChip pressed={ownedOnly} onclick={() => (ownedOnly = !ownedOnly)}
                >Owned</FilterChip
              >
              {#each countries.slice(0, 12) as c (c)}
                <FilterChip
                  pressed={countryFilter === c}
                  onclick={() => (countryFilter = countryFilter === c ? null : c)}>{c}</FilterChip
                >
              {/each}
              {#each formats.slice(0, 12) as f (f)}
                <FilterChip
                  pressed={formatFilter === f}
                  onclick={() => (formatFilter = formatFilter === f ? null : f)}>{f}</FilterChip
                >
              {/each}
            </div>
          </div>
          {#if versions.length === 0}
            <p class="text-muted-foreground text-subheadline md:text-sm">No versions match.</p>
          {:else}
            <ul class="flex flex-col">
              {#each versions as item (item.id)}
                <li>
                  <CatalogReleaseCard {item} href="/catalog/releases/{item.id}" />
                </li>
              {/each}
            </ul>
          {/if}
        </section>
      </div>
    {:catch err}
      <PageToolbarV2
        title="Master"
        back={{ label: 'Catalog', href: '/catalog?tab=masters' }}
        grouped
      />
      <div class="mx-auto flex w-full max-w-3xl flex-col gap-3 px-4 py-10 md:px-7" role="alert">
        <p class="text-destructive-text text-subheadline md:text-sm">
          {err instanceof Error ? err.message : 'Failed to load master'}
        </p>
        <Button variant="gray" class="w-fit rounded-full" href="/catalog?tab=masters"
          >Back to catalog</Button
        >
      </div>
    {/await}
  </ScrollArea>
</div>
