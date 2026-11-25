<script lang="ts">
  import { CircleCheck } from '@lucide/svelte';
  import { cn } from '#lib/utils.ts';

  type Props = {
    owned: boolean;
    class?: string;
    /** Compact pill for cards; default is an inline label. */
    compact?: boolean;
  };
  const { owned, class: className, compact = false }: Props = $props();
</script>

{#if owned}
  <span
    class={cn(
      'text-muted-foreground inline-flex items-center gap-1',
      compact ? 'text-caption-2 md:text-[11px]' : 'text-footnote md:text-xs',
      className
    )}
  >
    <CircleCheck class="text-primary size-3.5 shrink-0" aria-hidden="true" />
    <span>Owned</span>
  </span>
{/if}
