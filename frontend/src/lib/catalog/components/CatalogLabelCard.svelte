<script lang="ts">
  import type { CatalogLabelCardDto } from '#lib/catalog/types.ts';
  import { cn } from '#lib/utils.ts';

  type Props = {
    label: CatalogLabelCardDto;
    href: string;
    class?: string;
  };
  const { label, href, class: className }: Props = $props();
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
    {#if label.thumbnailUrl}
      <img
        src={label.thumbnailUrl}
        alt=""
        class="size-full object-cover"
        loading="lazy"
        decoding="async"
      />
    {/if}
  </div>
  <span class="min-w-0 flex-1 self-center">
    <span class="text-headline block truncate md:text-sm md:font-semibold">{label.name}</span>
    <span class="text-muted-foreground text-subheadline block truncate md:text-xs">
      {label.releaseCount} release{label.releaseCount === 1 ? '' : 's'}
    </span>
  </span>
</a>
