<script lang="ts">
  import { catalogImageSrc } from '#lib/catalog/api.ts';
  import type { CatalogImageDto } from '#lib/catalog/types.ts';
  import { cn } from '#lib/utils.ts';

  type Props = {
    images: CatalogImageDto[];
    class?: string;
  };
  const { images, class: className }: Props = $props();

  let active = $state(0);
  const current = $derived(images[active] ?? images[0] ?? null);
  const fullSrc = $derived(current ? catalogImageSrc(current, false) : null);
</script>

{#if images.length > 0 && current}
  <section class={cn('flex flex-col gap-3', className)} aria-label="Images">
    <div class="bg-muted relative aspect-square w-full max-w-md overflow-hidden rounded-xl">
      {#if fullSrc}
        <img
          src={fullSrc}
          alt={current.type === 'primary' ? 'Primary cover' : `Image (${current.type})`}
          class="size-full object-contain"
          decoding="async"
        />
      {/if}
    </div>
    {#if images.length > 1}
      <ul class="flex flex-wrap gap-2" role="list">
        {#each images as image, i (image.id)}
          {@const thumb = catalogImageSrc(image, true)}
          <li>
            <button
              type="button"
              class={cn(
                'bg-muted focus-visible:ring-ring size-14 overflow-hidden rounded-md focus-visible:ring-2 focus-visible:outline-hidden',
                active === i && 'ring-ring ring-2'
              )}
              aria-label={`Show image ${i + 1}`}
              aria-pressed={active === i}
              onclick={() => (active = i)}
            >
              {#if thumb}
                <img
                  src={thumb}
                  alt=""
                  class="size-full object-cover"
                  loading="lazy"
                  decoding="async"
                />
              {/if}
            </button>
          </li>
        {/each}
      </ul>
    {/if}
  </section>
{/if}
