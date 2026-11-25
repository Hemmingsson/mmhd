<script lang="ts">
  import { page } from '$app/state';
  import { ExternalLink } from '@lucide/svelte';
  import { fetchCatalogArtist } from '#lib/catalog/api.ts';
  import CatalogImageGallery from '#lib/catalog/components/CatalogImageGallery.svelte';
  import CatalogOwnedBadge from '#lib/catalog/components/CatalogOwnedBadge.svelte';
  import CatalogReleaseCard from '#lib/catalog/components/CatalogReleaseCard.svelte';
  import type { CatalogArtistDetailDto, CatalogDiscographyItemDto } from '#lib/catalog/types.ts';
  import { Button } from '#lib/components/ui/button/index.ts';
  import { ScrollArea } from '#lib/components/ui/scroll-area/index.ts';
  import { Skeleton } from '#lib/components/ui/skeleton/index.ts';
  import FilterChip from '#lib/components/v2/FilterChip.svelte';
  import PageToolbarV2 from '#lib/components/v2/PageToolbarV2.svelte';
  import { tabMemory } from '#lib/stores/tab-memory.svelte.ts';

  const id = $derived(Number(page.params.id));
  const detailPromise = $derived(
    Number.isFinite(id) ? fetchCatalogArtist(id) : Promise.reject(new Error('Invalid artist id'))
  );

  let roleFilter = $state<string | null>(null);

  function rolesOf(detail: CatalogArtistDetailDto): string[] {
    return [
      ...new Set(detail.discography.map((d) => d.role).filter((r): r is string => Boolean(r)))
    ];
  }

  function shownOf(detail: CatalogArtistDetailDto): CatalogDiscographyItemDto[] {
    return roleFilter
      ? detail.discography.filter((d) => d.role === roleFilter)
      : detail.discography;
  }

  function hrefFor(item: CatalogDiscographyItemDto): string {
    if (item.kind === 'master' && item.masterId != null) return `/catalog/masters/${item.masterId}`;
    if (item.releaseId != null) return `/catalog/releases/${item.releaseId}`;
    return '/catalog?tab=releases';
  }

  function rememberTitle(name: string) {
    tabMemory.setTitle(page.url, name);
  }

  function relationSections(detail: CatalogArtistDetailDto) {
    return [
      { label: 'Aliases', list: detail.aliases },
      { label: 'Members', list: detail.members },
      { label: 'Groups', list: detail.groups }
    ] as const;
  }
</script>

<div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
  <ScrollArea class="min-h-0 flex-1">
    {#await detailPromise}
      <PageToolbarV2
        title="Artist"
        meta="Loading…"
        back={{ label: 'Catalog', href: '/catalog' }}
        grouped
      />
      <div class="mx-auto flex w-full max-w-3xl flex-col gap-4 px-4 pb-10 md:px-7" aria-busy="true">
        <Skeleton class="aspect-square max-w-xs rounded-xl" />
        <Skeleton class="h-4 w-2/3" />
        <Skeleton class="h-4 w-1/2" />
      </div>
    {:then raw}
      {@const detail = (rememberTitle(raw.name), raw)}
      <PageToolbarV2
        title={detail.name}
        meta={`${detail.discographyTotal} release${detail.discographyTotal === 1 ? '' : 's'} · ${detail.ownership.ownedReleases} owned`}
        back={{ label: 'Catalog', href: '/catalog' }}
        grouped
      />

      <div class="mx-auto flex w-full max-w-3xl flex-col gap-8 px-4 pb-10 md:px-7">
        <CatalogImageGallery images={detail.images} />

        <div class="flex flex-col gap-2">
          {#if detail.realName}
            <p class="text-body md:text-sm">
              <span class="text-muted-foreground">Real name</span>
              {detail.realName}
            </p>
          {/if}
          {#if detail.profile}
            <p class="text-body whitespace-pre-wrap md:text-sm">{detail.profile}</p>
          {/if}
          {#if detail.nameVariations.length > 0}
            <p class="text-muted-foreground text-footnote md:text-xs">
              Also known as: {detail.nameVariations.join(' · ')}
            </p>
          {/if}
          {#if detail.libraryArtistNames.length > 0}
            <p class="text-footnote md:text-xs">
              <span class="text-muted-foreground">Library names:</span>
              {detail.libraryArtistNames.join(' · ')}
            </p>
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

        {#if detail.topStyles.length > 0}
          <section aria-labelledby="artist-styles">
            <h2 id="artist-styles" class="text-title-2 mb-2 md:text-lg">Top styles</h2>
            <div class="flex flex-wrap gap-2">
              {#each detail.topStyles as style (style.name)}
                <a
                  href="/catalog?tab=releases&style={encodeURIComponent(style.name)}"
                  class="bg-secondary text-subheadline rounded-full px-3 py-1.5 md:text-sm"
                  >{style.name}</a
                >
              {/each}
            </div>
          </section>
        {/if}

        {#each relationSections(detail) as section (section.label)}
          {#if section.list.length > 0}
            <section>
              <h2 class="text-title-2 mb-2 md:text-lg">{section.label}</h2>
              <ul class="flex flex-col gap-1">
                {#each section.list as rel (rel.artist.discogsId + rel.kind)}
                  <li>
                    {#if rel.artist.id != null}
                      <a
                        href="/catalog/artists/{rel.artist.id}"
                        class="text-primary text-body hover:underline md:text-sm"
                        >{rel.artist.name}</a
                      >
                    {:else}
                      <span class="text-body md:text-sm">{rel.artist.name}</span>
                    {/if}
                    {#if rel.active === false}
                      <span class="text-muted-foreground text-footnote md:text-xs"> (former)</span>
                    {/if}
                  </li>
                {/each}
              </ul>
            </section>
          {/if}
        {/each}

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

        <section aria-labelledby="artist-discography">
          <div class="mb-2 flex flex-wrap items-end justify-between gap-2">
            <h2 id="artist-discography" class="text-title-2 md:text-lg">Discography</h2>
            <CatalogOwnedBadge owned={detail.ownership.ownedReleases > 0} />
          </div>
          {#if rolesOf(detail).length > 1}
            <div class="mb-3 flex flex-wrap gap-2" role="group" aria-label="Role filter">
              <FilterChip pressed={roleFilter === null} onclick={() => (roleFilter = null)}
                >All</FilterChip
              >
              {#each rolesOf(detail) as role (role)}
                <FilterChip pressed={roleFilter === role} onclick={() => (roleFilter = role)}
                  >{role}</FilterChip
                >
              {/each}
            </div>
          {/if}
          <ul class="flex flex-col">
            {#each shownOf(detail) as item (item.kind + ':' + (item.releaseId ?? item.masterId ?? item.discogsId))}
              <li>
                <CatalogReleaseCard {item} href={hrefFor(item)} />
              </li>
            {/each}
          </ul>
        </section>
      </div>
    {:catch err}
      <PageToolbarV2 title="Artist" back={{ label: 'Catalog', href: '/catalog' }} grouped />
      <div class="mx-auto flex w-full max-w-3xl flex-col gap-3 px-4 py-10 md:px-7" role="alert">
        <p class="text-destructive-text text-subheadline md:text-sm">
          {err instanceof Error ? err.message : 'Failed to load artist'}
        </p>
        <Button variant="gray" class="w-fit rounded-full" href="/catalog">Back to catalog</Button>
      </div>
    {/await}
  </ScrollArea>
</div>
