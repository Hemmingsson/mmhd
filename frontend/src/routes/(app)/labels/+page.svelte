<script lang="ts">
  import { page } from '$app/state';
  import { songsStore } from '#lib/stores/songs.svelte.ts';
  import { groupByLabel, labelHref } from '#lib/library/labels.ts';
  import { albumHref } from '#lib/library/last-played.ts';
  import PageToolbarV2 from '#lib/components/v2/PageToolbarV2.svelte';
  import LibraryArtistsGridV2 from '#lib/components/v2/LibraryArtistsGridV2.svelte';
  import LibraryAlbumsGridV2 from '#lib/components/v2/LibraryAlbumsGridV2.svelte';
  import Cover from '#lib/components/file-browser/Cover.svelte';
  function portrait(name: string) {
    return name.toLowerCase() === 'kess kill' ? '/label-images/kess-kill.jpg' : null;
  }
  let search = $state('');
  const requested = $derived(page.url.searchParams.get('name')?.trim());
  const groups = $derived(groupByLabel(songsStore.albums));
  const selected = $derived(groups.find((g) => g.name.toLowerCase() === requested?.toLowerCase()));
  const visible = $derived(
    groups.filter((g) => g.name.toLowerCase().includes(search.trim().toLowerCase()))
  );
  const tiles = $derived(
    visible.map((group) => {
      const cover = group.albums.find((album) => album.coverUrl) ?? group.albums[0];
      return {
        key: group.name,
        label: group.name,
        albumCount: group.albums.length,
        trackCount: group.albums.reduce((n, album) => n + album.songs.length, 0),
        coverArtist: group.name,
        coverTitle: group.name,
        coverUrl: cover.coverUrl ?? null
      };
    })
  );

  $effect(() => {
    songsStore.revalidate();
    songsStore.startLive();
    return () => songsStore.stopLive();
  });
</script>

<div class="h-full overflow-y-auto">
  <PageToolbarV2 title={selected?.name ?? requested ?? 'Labels'} />
  <section class="mx-auto space-y-3 px-4 pb-12 md:px-7">
    {#if requested}
      <a class="text-sm underline" href="/labels">All labels</a>
    {:else}
      <input
        type="search"
        class="border-input bg-background w-full rounded-md border px-3 py-2"
        placeholder="Search labels"
        aria-label="Search labels"
        bind:value={search}
      />
    {/if}
    {#if songsStore.isLoading && !groups.length}
      <p>Loading releases…</p>
    {:else if songsStore.error && !groups.length}
      <button onclick={() => songsStore.revalidate()}>Retry loading the library</button>
    {:else if requested}
      {#if selected}
        <header class="flex items-center gap-5 py-5">
          <Cover
            artist={selected.name}
            title={selected.name}
            coverUrl={portrait(selected.name) ??
              selected.albums.find((album) => album.coverUrl)?.coverUrl}
            fallbackUrl={selected.albums.find((album) => album.coverUrl)?.coverUrl}
            size={120}
            corner={60}
            caption={false}
          />
          <div>
            <h1 class="text-2xl font-bold">{selected.name}</h1>
            <p class="text-muted-foreground">
              {selected.albums.length}
              {selected.albums.length === 1 ? 'release' : 'releases'}
            </p>
          </div>
        </header>
        <LibraryAlbumsGridV2 albums={selected.albums} hrefFor={albumHref} menu />
      {:else}
        <p>No releases in your library have this label.</p>
      {/if}
    {:else}
      <LibraryArtistsGridV2
        groups={tiles}
        entity="label"
        {portrait}
        hrefFor={(group) => labelHref(group.label)}
      >
        {#snippet empty()}<p>
            {search ? 'No labels match your search.' : 'No label credits in your library yet.'}
          </p>{/snippet}
      </LibraryArtistsGridV2>
    {/if}
  </section>
</div>
