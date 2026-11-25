<script lang="ts">
  import CatalogOwnedBadge from './CatalogOwnedBadge.svelte';
  import { catalogYearLabel } from '#lib/catalog/format.ts';
  import type { CatalogMasterCardDto } from '#lib/catalog/types.ts';
  import { cn } from '#lib/utils.ts';

  type Props = {
    master: CatalogMasterCardDto;
    href: string;
    class?: string;
  };
  const { master, href, class: className }: Props = $props();

  const subtitle = $derived(
    [
      master.artistDisplay,
      catalogYearLabel(master.year) !== '—' ? catalogYearLabel(master.year) : null,
      `${master.versionCount} version${master.versionCount === 1 ? '' : 's'}`
    ]
      .filter(Boolean)
      .join(' · ')
  );
</script>

<a
  {href}
  class={cn(
    'hover:bg-accent focus-visible:ring-ring flex gap-3 rounded-xl p-2 focus-visible:ring-2 focus-visible:outline-hidden',
    className
  )}
>
  <div
    class="bg-muted relative size-14 shrink-0 overflow-hidden rounded-md md:size-16"
    aria-hidden="true"
  >
    {#if master.thumbnailUrl}
      <img
        src={master.thumbnailUrl}
        alt=""
        class="size-full object-cover"
        loading="lazy"
        decoding="async"
      />
    {/if}
  </div>
  <span class="min-w-0 flex-1 self-center">
    <span class="text-headline flex items-center gap-2 truncate md:text-sm md:font-semibold">
      <span class="truncate">{master.title}</span>
      <CatalogOwnedBadge owned={master.owned} compact class="shrink-0" />
    </span>
    {#if subtitle}
      <span class="text-muted-foreground text-subheadline block truncate md:text-xs"
        >{subtitle}</span
      >
    {/if}
    {#if master.styles.length > 0}
      <span class="text-muted-foreground-dim text-caption-2 mt-0.5 block truncate md:text-[11px]">
        {master.styles.slice(0, 3).join(' · ')}
      </span>
    {/if}
  </span>
</a>
