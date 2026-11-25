<script lang="ts">
  import { Button } from '#lib/components/ui/button/index.ts';
  import { ChevronLeft, ChevronRight } from '@lucide/svelte';

  type Props = {
    page: number;
    pageSize: number;
    total: number;
    onpage: (page: number) => void;
  };
  const { page, pageSize, total, onpage }: Props = $props();

  const totalPages = $derived(Math.max(1, Math.ceil(total / Math.max(1, pageSize))));
  const from = $derived(total === 0 ? 0 : (page - 1) * pageSize + 1);
  const to = $derived(Math.min(total, page * pageSize));
</script>

{#if total > 0}
  <nav class="flex items-center justify-between gap-3 px-1 py-2" aria-label="Pagination">
    <p class="text-muted-foreground text-footnote md:text-xs">
      {from}–{to} of {total.toLocaleString()}
    </p>
    <div class="flex items-center gap-1">
      <Button
        variant="ghost"
        size="icon"
        class="size-9 rounded-full"
        disabled={page <= 1}
        aria-label="Previous page"
        onclick={() => onpage(page - 1)}
      >
        <ChevronLeft class="size-4" />
      </Button>
      <span class="text-subheadline min-w-16 text-center md:text-sm">
        {page} / {totalPages}
      </span>
      <Button
        variant="ghost"
        size="icon"
        class="size-9 rounded-full"
        disabled={page >= totalPages}
        aria-label="Next page"
        onclick={() => onpage(page + 1)}
      >
        <ChevronRight class="size-4" />
      </Button>
    </div>
  </nav>
{/if}
