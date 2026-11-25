<script lang="ts">
  import { SvelteMap } from 'svelte/reactivity';
  import { page } from '$app/state';
  import { isAdmin } from '#lib/auth/capabilities.ts';
  import DiscogsEditions from '#lib/catalog/components/DiscogsEditions.svelte';
  import {
    fetchLibraryProfile,
    countryName,
    profileParts,
    type LibraryProfile
  } from '#lib/library/library-profile.ts';
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
  let sort = $state<'newest' | 'oldest' | 'title'>('newest');
  const releases = $derived(
    (selected?.albums ?? []).toSorted((a, b) => {
      const ad = a.releaseDate || a.year?.toString(),
        bd = b.releaseDate || b.year?.toString();
      if (sort !== 'title') {
        if (!ad && bd) return 1;
        if (ad && !bd) return -1;
        const order = (ad ?? '').localeCompare(bd ?? '');
        if (order) return sort === 'newest' ? -order : order;
      }
      return a.title.localeCompare(b.title) || a.artist.localeCompare(b.artist);
    })
  );
  const visible = $derived(groups.filter((g) => key(g.name).includes(key(search))));
  const artwork = (g: Group) => portraits[key(g.name)];
  const tracks = $derived.by(() => {
    const result = new SvelteMap<number, { song: Album['songs'][number]; album: Album }>();
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
    const names = new SvelteMap<string, string>();
    for (const { song } of tracks) {
      for (const name of recordLabels(song.artists || song.artist)) names.set(key(name), name);
    }
    return [...names.values()].sort((a, b) => a.localeCompare(b));
  });
  let attempt = $state(0);
  const profile = $derived.by(() => {
    void attempt;
    return requested && isAdmin(page.data.user) ? fetchLibraryProfile('label', requested) : null;
  });
</script>

{#snippet heading(name: string, data: LibraryProfile | null)}
  <header>
    {#if data?.image}<img class="logo" src={data.image} alt={name + ' logo'} />{/if}
    <div>
      <h1>{name}</h1>
      {#if data?.country}<p>{countryName(data.country)}</p>{/if}
      {#if data?.profile}<p class="bio">
          {#each profileParts(data.profile) as part, i (i)}{#if part.href}<a
                href={part.href}
                target="_blank"
                rel="noreferrer">{part.text}</a
              >{:else}{part.text}{/if}{/each}
        </p>{/if}
      {#if data?.parent}<p>Parent label · <a href={data.parent.url}>{data.parent.name}</a></p>{/if}
      {#if data}<div class="links">
          {#if data.discogsUrl}<a href={data.discogsUrl} target="_blank" rel="noreferrer"
              >Discogs ↗</a
            >{/if}
          {#each data.links as link (link)}<a href={link} target="_blank" rel="noreferrer"
              >{new URL(link).hostname.replace(/^www\./, '')} ↗</a
            >{/each}
        </div>{/if}
      {#if selected}<small
          >{selected.albums.length} albums · {tracks.length} tracks · {artists.length} artists in your
          library</small
        >{/if}
    </div>
  </header>
{/snippet}

{#snippet image(name: string | null | undefined, url: string | null | undefined, circle: boolean)}
  <span class={['art', { circle }]} aria-hidden="true">
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
  {#if requested}
    <a href="/labels">All labels</a>
    {#await profile}
      {@render heading(requested, null)}
    {:then data}
      {@render heading(requested, data)}
      {#if data?.discogsId}{#key data.discogsId}<DiscogsEditions id={data.discogsId} />{/key}
      {:else if isAdmin(page.data.user)}<p>No confirmed Discogs label.</p>{/if}
    {:catch}
      {@render heading(requested, null)}
      <button onclick={() => attempt++}>Retry loading Discogs label</button>
    {/await}
    {#if selected}
      <div class="releases-heading">
        <h2>In your library</h2>
        <label
          >Sort <select bind:value={sort}>
            <option value="newest">Newest first</option><option value="oldest">Oldest first</option
            ><option value="title">Title A–Z</option>
          </select></label
        >
      </div>
      {#each releases as album (album.key)}
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
    {:else if loading}<p>Loading library…</p>{:else if error}<button onclick={retry}
        >Retry loading library</button
      >{:else}<p>No releases in your library have this label.</p>{/if}
  {:else if loading && !groups.length}<p>Loading labels…</p>
  {:else if error && !groups.length}<button onclick={retry}>Retry loading labels</button>
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
  .releases-heading {
    margin-top: 1.75rem;
    display: flex;
    gap: 1rem;
    align-items: baseline;
    justify-content: space-between;
  }
  .releases-heading h2 {
    margin: 0;
  }
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
  header .logo {
    object-fit: contain;
    align-self: flex-start;
    max-height: 9rem;
    width: clamp(5rem, 15vw, 9rem);
    font-size: 2rem;
  }
  .bio {
    white-space: pre-line;
    color: inherit;
  }
  header a {
    color: var(--primary);
    text-decoration: underline;
  }
  .links {
    display: flex;
    flex-wrap: wrap;
    gap: 0.2rem 0.75rem;
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
