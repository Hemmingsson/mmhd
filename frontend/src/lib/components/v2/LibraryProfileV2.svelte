<script lang="ts">
  import {
    countryName,
    fetchLibraryProfile,
    profileParts,
    type ProfileLink
  } from '#lib/library/library-profile.ts';
  let { kind, name }: { kind: 'artist' | 'label'; name: string } = $props();
  const profile = $derived(fetchLibraryProfile(kind, name));
  function linkName(url: string) {
    return new URL(url).hostname.replace(/^www\./, '');
  }
</script>

{#snippet related(title: string, links: ProfileLink[])}
  {#if links.length}<div>
      <dt>{title}</dt>
      <dd>
        {#each links as link, i (link.url)}
          {#if i > 0},
          {/if}<a href={link.url} target="_blank" rel="noreferrer">{link.name}</a>
        {/each}
      </dd>
    </div>{/if}
{/snippet}

{#await profile}
  <p class="text-muted-foreground text-sm">Loading profile…</p>
{:then data}
  {#if data}
    <section aria-label="{kind} profile" class="profile">
      {#if data.image}
        <a class="portrait" href={data.image} target="_blank" rel="noreferrer">
          <img
            src={data.image}
            alt="{name} profile"
            loading="lazy"
            onerror={(e) => ((e.currentTarget as HTMLImageElement).hidden = true)}
          />
        </a>
      {/if}
      <dl>
        {#if data.realName}<div>
            <dt>Real name</dt>
            <dd>{data.realName}</dd>
          </div>{/if}
        {#if data.country}<div>
            <dt>Country</dt>
            <dd>{countryName(data.country)}</dd>
          </div>{/if}
        {#if data.profile}<div>
            <dt>Profile</dt>
            <dd class="whitespace-pre-line">
              {#each profileParts(data.profile) as part, i (i)}{#if part.href}<a
                    href={part.href}
                    target="_blank"
                    rel="noreferrer">{part.text}</a
                  >{:else}{part.text}{/if}{/each}
            </dd>
          </div>{/if}
        {@render related('Parent label', data.parent ? [data.parent] : [])}
        {@render related('Aliases', data.aliases)}
        {@render related('In groups', data.groups)}
        {#if data.variations.length}<div>
            <dt>Name variations</dt>
            <dd>{data.variations.join(', ')}</dd>
          </div>{/if}
        {#if data.links.length || data.discogsUrl}<div>
            <dt>Links</dt>
            <dd class="flex flex-wrap gap-x-3">
              {#if data.discogsUrl}<a href={data.discogsUrl} target="_blank" rel="noreferrer"
                  >Discogs ↗</a
                >{/if}
              {#each data.links as link (link)}<a href={link} target="_blank" rel="noreferrer"
                  >{linkName(link)} ↗</a
                >{/each}
            </dd>
          </div>{/if}
      </dl>
    </section>
  {:else}
    <p class="text-muted-foreground text-sm">No confirmed {kind} profile.</p>
  {/if}
{:catch}
  <p class="text-muted-foreground text-sm">Profile information is temporarily unavailable.</p>
{/await}

<style>
  .profile {
    display: flex;
    align-items: flex-start;
    gap: 1rem;
    flex-wrap: wrap;
  }
  .portrait img {
    width: clamp(6rem, 16vw, 10rem);
    max-height: 12rem;
    object-fit: contain;
    border-radius: 0.4rem;
  }
  dl {
    flex: 1;
    min-width: min(100%, 16rem);
    display: grid;
    gap: 0.5rem;
  }
  dl div {
    display: grid;
    grid-template-columns: minmax(5rem, 7rem) 1fr;
    gap: 0.75rem;
  }
  dt {
    color: var(--muted-foreground);
  }
  dd {
    min-width: 0;
    overflow-wrap: anywhere;
  }
  a {
    color: var(--primary);
    text-decoration: underline;
    text-underline-offset: 0.15em;
  }
</style>
