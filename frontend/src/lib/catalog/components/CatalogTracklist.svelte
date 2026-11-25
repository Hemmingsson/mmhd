<script lang="ts">
  import CatalogOwnedBadge from './CatalogOwnedBadge.svelte';
  import { creditDisplayName } from '#lib/catalog/credits.ts';
  import { catalogTrackDuration } from '#lib/catalog/format.ts';
  import type { CatalogTrackDto } from '#lib/catalog/types.ts';
  import { cn } from '#lib/utils.ts';

  type Props = {
    tracks: CatalogTrackDto[];
    class?: string;
  };
  const { tracks, class: className }: Props = $props();
</script>

{#snippet trackRow(track: CatalogTrackDto, depth: number)}
  {#if track.type === 'heading'}
    <li
      class={cn('text-title-3 pt-3 pb-1 font-semibold first:pt-0 md:text-sm', depth > 0 && 'pl-4')}
      role="presentation"
    >
      {track.title}
    </li>
    {#each track.subTracks as sub (sub.sequence + ':' + (sub.position ?? '') + ':' + sub.title)}
      {@render trackRow(sub, depth + 1)}
    {/each}
  {:else}
    <li
      class={cn(
        'border-separator flex flex-col gap-0.5 border-b py-2 last:border-b-0',
        depth > 0 && 'pl-4'
      )}
    >
      <div class="flex items-baseline gap-3">
        <span class="text-muted-foreground text-footnote w-10 shrink-0 tabular-nums md:text-xs"
          >{track.position ?? ''}</span
        >
        <span class="text-body min-w-0 flex-1 md:text-sm">
          <span class="inline-flex max-w-full items-center gap-2">
            <span class="truncate">{track.title}</span>
            <CatalogOwnedBadge owned={track.owned} compact class="shrink-0" />
          </span>
        </span>
        <span class="text-muted-foreground text-footnote shrink-0 tabular-nums md:text-xs">
          {catalogTrackDuration(track)}
        </span>
      </div>
      {#if track.credits.length > 0}
        <p class="text-muted-foreground text-caption-2 pl-[3.25rem] md:text-[11px]">
          {track.credits
            .map((c) => {
              const name = creditDisplayName(c);
              return c.role ? `${name} (${c.role})` : name;
            })
            .join(' · ')}
        </p>
      {/if}
      {#if track.subTracks.length > 0}
        <ul class="pl-2">
          {#each track.subTracks as sub (sub.sequence + ':' + (sub.position ?? '') + ':' + sub.title)}
            {@render trackRow(sub, depth + 1)}
          {/each}
        </ul>
      {/if}
    </li>
  {/if}
{/snippet}

{#if tracks.length > 0}
  <ul class={cn('flex flex-col', className)} aria-label="Tracklist">
    {#each tracks as track (track.sequence + ':' + (track.position ?? '') + ':' + track.title)}
      {@render trackRow(track, 0)}
    {/each}
  </ul>
{/if}
