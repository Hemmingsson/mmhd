<script lang="ts">
  import { groupByLabel, recordLabels } from '#lib/library/labels.ts';
  type Group = ReturnType<typeof groupByLabel>[number];
  type Album = Group['albums'][number];
  let {
    groups,
    requested,
    loading,
    error,
    retry,
    portraits
  }: {
    groups: Group[];
    requested?: string;
    loading: boolean;
    error: string | null;
    retry: () => void;
    portraits: Record<string, string>;
  } = $props();
  let search = $state('');
  const key = (s?: string | null) => (s ?? '').trim().toLowerCase();
  const selected = $derived(groups.find((g) => key(g.name) === key(requested)));
  const visible = $derived(groups.filter((g) => key(g.name).includes(key(search))));
  const artwork = (g: Group) =>
    portraits[key(g.name)] || g.albums.find((a) => a.coverUrl)?.coverUrl;
  const tracks = $derived.by(() => {
    const result = new Map<number, { song: Album['songs'][number]; album: Album }>();
    for (const album of selected?.albums ?? []) {
      for (const song of album.songs) {
        if (
          recordLabels(song.label || album.label).some(
            (label) => key(label) === key(selected?.name)
          )
        ) {
          result.set(song.id, { song, album });
        }
      }
    }
    return [...result.values()];
  });
  const artists = $derived.by(() => {
    const names = new Map<string, string>();
    for (const { song } of tracks) {
      for (const name of recordLabels(song.artists || song.artist)) names.set(key(name), name);
    }
    return [...names.values()].sort((a, b) => a.localeCompare(b));
  });
</script>

{#snippet image(name: string | null | undefined, url: string | null | undefined, circle: boolean)}
  <span class="art" class:circle aria-hidden="true">
    <span>{name?.trim().slice(0, 1) || '♪'}</span>
    {#if url}{#key url}<img
          src={url}
          alt=""
          loading="lazy"
          onerror={(e) => ((e.currentTarget as HTMLImageElement).hidden = true)}
        />{/key}{/if}
  </span>
{/snippet}

<section class="label-page">
  {#if loading && !groups.length}<p>Loading labels…</p>
  {:else if error && !groups.length}<button onclick={retry}>Retry loading labels</button>
  {:else if requested}
    <a href="/labels">All labels</a>
    {#if selected}
      <header>
        {@render image(selected.name, artwork(selected), true)}
        <div>
          <h1>{selected.name}</h1>
          <p>{selected.albums.length} albums · {tracks.length} tracks · {artists.length} artists</p>
        </div>
      </header>
      <h2>Albums</h2>
      {#each selected.albums as album (album.key)}
        <a class="item" href={'/library?album=' + encodeURIComponent(album.key)}>
          {@render image(album.title, album.coverUrl, false)}
          <span
            ><strong>{album.title}</strong><small
              >{[album.artist, album.year].filter(Boolean).join(' · ')}</small
            ></span
          >
        </a>
      {/each}
      <h2>Tracks</h2>
      {#each tracks as { song, album } (song.id)}
        <a class="item" href={'/library?song=' + song.id}>
          {@render image(song.title, album.coverUrl, false)}
          <span
            ><strong>{song.title}</strong><small
              >{[song.artist, album.title].filter(Boolean).join(' · ')}</small
            ></span
          >
        </a>
      {:else}<p>No credited tracks.</p>{/each}
      <h2>Artists</h2>
      {#each artists as name (name)}
        <a class="item" href={'/library?artist=' + encodeURIComponent(name)}>
          {@render image(
            name,
            '/api/mh/api/artists/image?v=2&name=' + encodeURIComponent(name),
            true
          )}
          <strong>{name}</strong>
        </a>
      {:else}<p>No credited artists.</p>{/each}
    {:else}<p>No releases in your library have this label.</p>{/if}
  {:else}
    <input
      type="search"
      placeholder="Search labels"
      aria-label="Search labels"
      bind:value={search}
    />
    <div class="tiles">
      {#each visible as group (group.name)}
        <a class="tile" href={'/labels?name=' + encodeURIComponent(group.name)}>
          {@render image(group.name, artwork(group), true)}
          <strong>{group.name}</strong><small
            >{group.albums.length} {group.albums.length === 1 ? 'album' : 'albums'}</small
          >
        </a>
      {:else}<p>No matching labels.</p>{/each}
    </div>
  {/if}
</section>

<style>
  .label-page {
    padding: 1rem clamp(1rem, 3vw, 2rem) 5rem;
    max-width: 74rem;
    margin: auto;
  }
  a {
    color: inherit;
    text-decoration: none;
  }
  a:hover strong {
    text-decoration: underline;
  }
  header,
  .item {
    display: flex;
    align-items: center;
    gap: 1rem;
  }
  header {
    padding: 1.5rem 0;
    gap: clamp(1rem, 3vw, 2rem);
  }
  h1 {
    font-size: clamp(1.8rem, 5vw, 3rem);
    line-height: 1.05;
    font-weight: 750;
    overflow-wrap: anywhere;
  }
  h2 {
    font-size: 1.35rem;
    font-weight: 700;
    margin: 1.75rem 0 0.4rem;
  }
  small,
  header p {
    display: block;
    color: var(--muted-foreground);
    margin-top: 0.2rem;
  }
  .item {
    min-height: 3.8rem;
    border-bottom: 1px solid var(--border);
    padding: 0.4rem 0;
  }
  .art {
    position: relative;
    display: grid;
    place-items: center;
    overflow: hidden;
    background: var(--muted);
    color: var(--muted-foreground);
    width: 2.8rem;
    aspect-ratio: 1;
    flex: none;
    border-radius: 0.4rem;
  }
  .art img {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
    object-fit: cover;
  }
  .art :global(img[hidden]) {
    display: none;
  }
  .circle {
    border-radius: 50%;
  }
  header .art {
    width: clamp(5rem, 15vw, 9rem);
    font-size: 2rem;
  }
  input {
    width: 100%;
    border: 1px solid var(--border);
    border-radius: 0.5rem;
    padding: 0.6rem 0.8rem;
  }
  .tiles {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(8rem, 1fr));
    gap: 1.25rem;
    margin-top: 1.25rem;
  }
  .tile {
    text-align: center;
    min-width: 0;
    overflow-wrap: anywhere;
  }
  .tile .art {
    width: 100%;
    margin-bottom: 0.5rem;
  }
  @media (max-width: 600px) {
    .tiles {
      display: flex;
      flex-direction: column;
      gap: 0;
    }
    .tile {
      display: grid;
      grid-template-columns: 3rem 1fr;
      text-align: start;
      column-gap: 0.8rem;
      padding: 0.5rem 0;
      border-bottom: 1px solid var(--border);
    }
    .tile .art {
      grid-row: span 2;
      margin: 0;
    }
    .tile small {
      margin: 0;
    }
  }
</style>
