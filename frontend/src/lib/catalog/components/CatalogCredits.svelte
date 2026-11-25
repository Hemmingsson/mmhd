<script lang="ts">
  import { creditDisplayName, groupCreditsByRole } from '#lib/catalog/credits.ts';
  import type { CatalogCreditDto } from '#lib/catalog/types.ts';
  import { cn } from '#lib/utils.ts';

  type Props = {
    credits: CatalogCreditDto[];
    title?: string;
    class?: string;
  };
  const { credits, title = 'Credits', class: className }: Props = $props();

  const groups = $derived(groupCreditsByRole(credits));
</script>

{#if credits.length > 0}
  <section class={cn('flex flex-col gap-3', className)} aria-labelledby="catalog-credits-heading">
    <h2 id="catalog-credits-heading" class="text-title-2 md:text-lg">{title}</h2>
    <dl class="flex flex-col gap-3">
      {#each groups as group (group.role)}
        <div>
          <dt class="text-muted-foreground text-footnote mb-1 md:text-xs">{group.role}</dt>
          <dd class="text-body flex flex-wrap gap-x-2 gap-y-1 md:text-sm">
            {#each group.credits as credit, i (credit.artist.discogsId + ':' + i)}
              {@const name = creditDisplayName(credit)}
              {#if credit.artist.id != null}
                <a
                  href="/catalog/artists/{credit.artist.id}"
                  class="text-primary focus-visible:ring-ring rounded-sm hover:underline focus-visible:ring-2 focus-visible:outline-hidden"
                  >{name}</a
                >
              {:else}
                <span>{name}</span>
              {/if}
              {#if credit.tracks}
                <span class="text-muted-foreground text-footnote md:text-xs">({credit.tracks})</span
                >
              {/if}
              {#if i < group.credits.length - 1}<span
                  class="text-muted-foreground"
                  aria-hidden="true">·</span
                >{/if}
            {/each}
          </dd>
        </div>
      {/each}
    </dl>
  </section>
{/if}
