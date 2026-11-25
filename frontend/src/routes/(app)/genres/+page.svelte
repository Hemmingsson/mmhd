<script lang="ts">
  import { page } from '$app/state';
  import { songsStore } from '#lib/stores/songs.svelte.ts';
  import PageToolbarV2 from '#lib/components/v2/PageToolbarV2.svelte';
  import { albumHref } from '#lib/library/last-played.ts';
  import { normalizeDiscogsGenres } from '#lib/library/discogs-genres.ts';
  import { genreIndex, genreMatches } from '#lib/library/genre-browser.ts';
  let query = $state('');
  const index = $derived(genreIndex(songsStore.albums));
  const selected = $derived([
    ...new Set(page.url.searchParams.getAll('style').flatMap(normalizeDiscogsGenres))
  ]);
  const any = $derived(page.url.searchParams.get('match') === 'any');
  const tab = $derived(page.url.searchParams.get('view') === 'tracks' ? 'tracks' : 'releases');
  const pageSize = 60;
  const limit = $derived(
    Math.max(pageSize, Number(page.url.searchParams.get('limit')) || pageSize)
  );
  const visibleTags = $derived(
    index.counts.filter(([tag]) => tag.toLowerCase().includes(query.trim().toLowerCase()))
  );
  const releases = $derived(
    index.releases.filter((r) => r.tags.size && genreMatches(r.tags, selected, any))
  );
  const tracks = $derived(
    index.releases.flatMap((r) =>
      r.tracks.filter((t) => t.styles.length && genreMatches(t.styles, selected, any))
    )
  );
  function href(styles = selected, mode = any, view = tab, size = pageSize) {
    const params = new URLSearchParams();
    for (const style of styles) params.append('style', style);
    if (mode) params.set('match', 'any');
    if (view === 'tracks') params.set('view', view);
    if (size > pageSize) params.set('limit', String(size));
    return '/genres' + (params.size ? '?' + params : '');
  }
  $effect(() => {
    songsStore.revalidate();
    songsStore.startLive();
    return () => songsStore.stopLive();
  });
</script>

<div class="h-full overflow-y-auto">
  <PageToolbarV2 title="Genres & styles" />
  <section class="mx-auto max-w-5xl space-y-5 px-4 pt-2 pb-16 md:px-7">
    {#if songsStore.isLoading && !index.counts.length}<p>Loading the library…</p>
    {:else if songsStore.error && !index.counts.length}<button
        onclick={() => songsStore.revalidate()}>Retry loading the library</button
      >
    {:else if !index.counts.length}<p>No Discogs genres or styles found in your library.</p>
    {:else}
      <input
        type="search"
        placeholder="Find a genre or style"
        aria-label="Find a genre or style"
        bind:value={query}
      />
      {#if selected.length}
        <div class="chips" aria-label="Selected styles">
          {#each selected as tag (tag)}<a
              class="chip chosen"
              href={href(selected.filter((t) => t !== tag))}
              aria-label="Remove {tag}">{tag} ×</a
            >{/each}
          <a href={href([])}>Clear</a>
        </div>
      {/if}
      <div class="chips choices" aria-label="Choose genres and styles">
        {#each visibleTags as [tag, count] (tag)}
          <a
            class="chip"
            class:chosen={selected.includes(tag)}
            aria-current={selected.includes(tag) ? 'true' : undefined}
            href={href(
              selected.includes(tag) ? selected.filter((t) => t !== tag) : [...selected, tag]
            )}
          >
            {tag} <span>{count}</span>
          </a>
        {:else}<p>No matching styles.</p>{/each}
      </div>
      {#if selected.length > 1}<nav class="chips" aria-label="Style matching">
          <a
            class:active={!any}
            aria-current={!any ? 'true' : undefined}
            href={href(selected, false)}>Match all</a
          >
          <a class:active={any} aria-current={any ? 'true' : undefined} href={href(selected, true)}
            >Match any</a
          >
        </nav>{/if}
      <nav class="tabs" aria-label="Matching music">
        <a class:active={tab === 'releases'} href={href(selected, any, 'releases')}
          >Releases · {releases.length}</a
        >
        <a class:active={tab === 'tracks'} href={href(selected, any, 'tracks')}
          >Tracks · {tracks.length}</a
        >
      </nav>
      {#if tab === 'releases'}
        {#each releases.slice(0, limit) as { album, tags } (album.key)}
          <a class="result" href={albumHref(album)}>
            {#if album.coverUrl}<img src={album.coverUrl} alt="" loading="lazy" />{/if}
            <div>
              <strong>{album.title}</strong><small>{album.artist} · {[...tags].join(' · ')}</small>
            </div>
          </a>
        {:else}<p>No releases match this selection.</p>{/each}
      {:else}
        {#each tracks.slice(0, limit) as { song, styles } (song.id)}
          <a class="result" href={'/library?song=' + song.id}
            ><div>
              <strong>{song.title || song.fileName}</strong>
              <small>{song.artist} · {styles.join(' · ')}</small>
            </div></a
          >
        {:else}<p>No tracks match this selection.</p>{/each}
      {/if}
      {#if (tab === 'tracks' ? tracks.length : releases.length) > limit}<a
          class="more"
          href={href(selected, any, tab, limit + pageSize)}>Show more</a
        >{/if}
    {/if}
  </section>
</div>

<style>
  input {
    width: 100%;
    border: 1px solid var(--border);
    border-radius: 0.55rem;
    padding: 0.65rem 0.9rem;
  }
  .chips,
  .tabs {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.5rem;
  }
  .choices {
    max-height: 16rem;
    overflow: auto;
  }
  .chip {
    border: 1px solid var(--border);
    border-radius: 2rem;
    padding: 0.25rem 0.7rem;
  }
  .chip span,
  small {
    font-size: 0.78rem;
    color: var(--muted-foreground);
  }
  .chip.chosen {
    color: var(--primary-foreground);
    background: var(--primary);
  }
  .chosen span {
    color: inherit;
  }
  .tabs {
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.65rem;
    gap: 1.25rem;
  }
  .active {
    text-decoration: underline;
    font-weight: 600;
  }
  .result {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin: 0;
    padding: 0.5rem 0;
    border-bottom: 1px solid var(--border);
  }
  .result:hover strong,
  .more:hover {
    text-decoration: underline;
  }
  .result img {
    width: 2.75rem;
    height: 2.75rem;
    object-fit: cover;
    border-radius: 0.25rem;
  }
  small {
    display: block;
  }
  .more {
    display: inline-block;
    padding: 0.5rem 0;
  }
</style>
