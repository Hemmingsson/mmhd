<script lang="ts">
  import { ExternalLink, LoaderCircle } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import { acquireCatalogRelease, fetchCatalogAvailability } from '#lib/catalog/api.ts';
  import { formatConfidence } from '#lib/catalog/format.ts';
  import type {
    CatalogAcquireResultDto,
    CatalogAvailabilityDto,
    CatalogPurchaseLinkDto
  } from '#lib/catalog/types.ts';
  import { Button } from '#lib/components/ui/button/index.ts';
  import { Skeleton } from '#lib/components/ui/skeleton/index.ts';
  import { cn } from '#lib/utils.ts';

  type Props = {
    releaseId: number;
    class?: string;
  };
  const { releaseId, class: className }: Props = $props();

  let reloadToken = $state(0);
  let busy = $state(false);
  let acquireResult = $state<CatalogAcquireResultDto | null>(null);
  let selectedEdition = $state<string | null>(null);

  const availabilityPromise = $derived.by(() => {
    void reloadToken;
    return fetchCatalogAvailability(releaseId);
  });

  function linksFor(availability: CatalogAvailabilityDto): CatalogPurchaseLinkDto[] {
    if (acquireResult?.status === 'unavailable') return acquireResult.purchaseLinks;
    return availability.purchaseLinks;
  }

  function editionId(availability: CatalogAvailabilityDto): string | null {
    return selectedEdition ?? availability.editions[0]?.editionId ?? null;
  }

  async function acquire(availability: CatalogAvailabilityDto) {
    busy = true;
    try {
      const result = await acquireCatalogRelease(releaseId, {
        providerEditionId: editionId(availability)
      });
      acquireResult = result;
      if (result.status === 'queued') {
        toast.success(result.queued === 1 ? '1 track queued' : `${result.queued} tracks queued`);
        selectedEdition = null;
        reloadToken += 1;
      } else if (result.status === 'already-owned') {
        toast.message(result.message ?? 'Already in your library');
        selectedEdition = null;
        reloadToken += 1;
      } else if (result.status === 'unavailable') {
        toast.message(result.message ?? 'Not available to download — see purchase links');
      }
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Acquire failed');
    } finally {
      busy = false;
    }
  }

  function statusLabel(status: string): string {
    switch (status) {
      case 'owned':
        return 'In your library';
      case 'available':
        return 'Available to acquire';
      case 'unavailable':
        return 'Not available from providers';
      default:
        return status;
    }
  }
</script>

{#snippet links(list: CatalogPurchaseLinkDto[])}
  {#if list.length > 0}
    <ul class="flex flex-col gap-1.5">
      {#each list as link (link.url)}
        <li>
          <a
            href={link.url}
            target="_blank"
            rel="noopener noreferrer"
            class="text-primary text-subheadline inline-flex items-center gap-1.5 hover:underline md:text-sm"
          >
            {link.name}
            <ExternalLink class="size-3.5" aria-hidden="true" />
            <span class="text-muted-foreground text-caption-2 md:text-[11px]">({link.kind})</span>
          </a>
        </li>
      {/each}
    </ul>
  {/if}
{/snippet}

<section
  class={cn('border-separator flex flex-col gap-3 rounded-xl border p-4', className)}
  aria-labelledby="catalog-availability-heading"
>
  {#await availabilityPromise}
    <div>
      <h2 id="catalog-availability-heading" class="text-title-2 md:text-lg">Availability</h2>
    </div>
    <div class="flex flex-col gap-2" aria-busy="true" aria-label="Loading availability">
      <Skeleton class="h-4 w-2/3" />
      <Skeleton class="h-4 w-1/2" />
    </div>
  {:then availability}
    <div class="flex items-start justify-between gap-3">
      <div>
        <h2 id="catalog-availability-heading" class="text-title-2 md:text-lg">Availability</h2>
        <p class="text-muted-foreground text-footnote md:text-xs">
          {statusLabel(availability.status)}
          · {availability.ownership.ownedSongs} owned song{availability.ownership.ownedSongs === 1
            ? ''
            : 's'}
        </p>
      </div>
      {#if availability.status === 'available'}
        <Button
          variant="tinted"
          size="sm"
          class="rounded-full"
          disabled={busy}
          onclick={() => void acquire(availability)}
        >
          {#if busy}<LoaderCircle class="size-4 animate-spin" />{/if}
          Acquire
        </Button>
      {/if}
    </div>

    {#if availability.editions.length > 0}
      <fieldset class="flex flex-col gap-2">
        <legend class="text-muted-foreground text-footnote mb-1 md:text-xs"
          >Provider editions</legend
        >
        {#each availability.editions as edition, i (edition.editionId)}
          <label class="hover:bg-accent flex cursor-pointer items-start gap-3 rounded-lg p-2">
            <input
              type="radio"
              name="catalog-edition-{releaseId}"
              class="mt-1"
              value={edition.editionId}
              checked={editionId(availability) === edition.editionId ||
                (selectedEdition == null && i === 0)}
              onchange={() => (selectedEdition = edition.editionId)}
            />
            <span class="min-w-0 flex-1">
              <span class="text-body block truncate md:text-sm">
                {edition.title}
                {#if edition.artist}<span class="text-muted-foreground">
                    — {edition.artist}</span
                  >{/if}
              </span>
              <span class="text-muted-foreground text-caption-2 block md:text-[11px]">
                {edition.provider} · confidence {formatConfidence(edition.confidence)}
                {#if edition.trackCount != null}
                  · {edition.trackCount} tracks
                {/if}
                {#if edition.year != null}
                  · {edition.year}
                {/if}
              </span>
            </span>
          </label>
        {/each}
      </fieldset>
    {/if}

    {#if linksFor(availability).length > 0}
      <div>
        <p class="text-muted-foreground text-footnote mb-1.5 md:text-xs">
          {acquireResult?.status === 'unavailable' ? 'Where to buy or stream' : 'Purchase / stream'}
        </p>
        {@render links(linksFor(availability))}
      </div>
    {/if}
  {:catch err}
    <div>
      <h2 id="catalog-availability-heading" class="text-title-2 md:text-lg">Availability</h2>
      <p class="text-destructive-text text-subheadline md:text-sm" role="alert">
        {err instanceof Error ? err.message : 'Failed to load availability'}
      </p>
      <Button
        variant="gray"
        size="sm"
        class="mt-2 w-fit rounded-full"
        onclick={() => (reloadToken += 1)}>Retry</Button
      >
    </div>
  {/await}
</section>
