<script lang="ts">
  import CatalogOwnedBadge from './CatalogOwnedBadge.svelte';
  import { catalogYearLabel } from '#lib/catalog/format.ts';
  import type { CatalogDiscographyItemDto, CatalogReleaseCardDto } from '#lib/catalog/types.ts';
  import { cn } from '#lib/utils.ts';

  type Card = CatalogReleaseCardDto | CatalogDiscographyItemDto;

  type Props = {
    item: Card;
    href: string;
    class?: string;
  };
  const { item, href, class: className }: Props = $props();

  const thumb = $derived(item.thumbnailUrl);
  const subtitle = $derived(
    [
      item.artistDisplay,
      catalogYearLabel(item.year) !== '—' ? catalogYearLabel(item.year) : null,
      'format' in item ? item.format : null,
      'role' in item ? item.role : null
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
    {#if thumb}
      <img src={thumb} alt="" class="size-full object-cover" loading="lazy" decoding="async" />
    {/if}
  </div>
  <span class="min-w-0 flex-1 self-center">
    <span class="text-headline flex items-center gap-2 truncate md:text-sm md:font-semibold">
      <span class="truncate">{item.title}</span>
      <CatalogOwnedBadge owned={item.owned} compact class="shrink-0" />
    </span>
    {#if subtitle}
      <span class="text-muted-foreground text-subheadline block truncate md:text-xs"
        >{subtitle}</span
      >
    {/if}
  </span>
</a>
