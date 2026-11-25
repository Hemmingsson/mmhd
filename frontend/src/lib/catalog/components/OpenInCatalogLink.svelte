<script lang="ts">
  import { ExternalLink } from '@lucide/svelte';
  import { fetchLibraryArtistId } from '#lib/catalog/api.ts';

  type Props = {
    /** Library artist name used for `GET /api/catalog/library-artist?name=`. */
    name: string;
  };
  const { name }: Props = $props();

  const lookup = $derived.by(() => {
    const artistName = name.trim();
    if (!artistName) return Promise.resolve(null as number | null);
    return fetchLibraryArtistId(artistName);
  });
</script>

{#await lookup then id}
  {#if id != null}
    <a
      href="/catalog/artists/{id}"
      class="text-primary text-subheadline focus-visible:ring-ring inline-flex items-center gap-1.5 rounded-sm hover:underline focus-visible:ring-2 focus-visible:outline-hidden md:text-sm"
    >
      Open in catalog
      <ExternalLink class="size-3.5" aria-hidden="true" />
    </a>
  {/if}
{/await}
