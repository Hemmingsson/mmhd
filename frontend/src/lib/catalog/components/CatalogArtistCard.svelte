<script lang="ts">
  import type { CatalogArtistCardDto } from '#lib/catalog/types.ts';
  import { cn } from '#lib/utils.ts';

  type Props = {
    artist: CatalogArtistCardDto;
    href: string;
    class?: string;
  };
  const { artist, href, class: className }: Props = $props();

  const meta = $derived(
    `${artist.releaseCount} release${artist.releaseCount === 1 ? '' : 's'}${
      artist.ownedReleaseCount > 0 ? ` · ${artist.ownedReleaseCount} owned` : ''
    }`
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
    class="bg-muted relative size-14 shrink-0 overflow-hidden rounded-full md:size-16"
    aria-hidden="true"
  >
    {#if artist.thumbnailUrl}
      <img
        src={artist.thumbnailUrl}
        alt=""
        class="size-full object-cover"
        loading="lazy"
        decoding="async"
      />
    {/if}
  </div>
  <span class="min-w-0 flex-1 self-center">
    <span class="text-headline block truncate md:text-sm md:font-semibold">{artist.name}</span>
    <span class="text-muted-foreground text-subheadline block truncate md:text-xs">{meta}</span>
  </span>
</a>
