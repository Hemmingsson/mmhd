<script lang="ts">
  import { page } from '$app/state';
  import { onMount } from 'svelte';
  import { LoaderCircle, TriangleAlert } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import {
    chooseIdentity,
    fetchBackfillRuns,
    fetchCatalogStatus,
    fetchIdentityReview,
    ingestCatalogEntity,
    parseDiscogsRef,
    rejectIdentity,
    rollbackBackfill,
    runBackfillBatch,
    startBackfill
  } from '#lib/catalog/api.ts';
  import type {
    CatalogBackfillKind,
    CatalogBackfillRunDto,
    CatalogEntityKind,
    CatalogIdentityDecisionDto,
    CatalogIdentitySubject,
    CatalogStatusDto
  } from '#lib/catalog/types.ts';
  import { formatConfidence } from '#lib/catalog/format.ts';
  import { Button } from '#lib/components/ui/button/index.ts';
  import { Input } from '#lib/components/ui/input/index.ts';
  import { Label } from '#lib/components/ui/label/index.ts';
  import { ScrollArea } from '#lib/components/ui/scroll-area/index.ts';
  import { Skeleton } from '#lib/components/ui/skeleton/index.ts';
  import { Switch } from '#lib/components/ui/switch/index.ts';
  import PageToolbarV2 from '#lib/components/v2/PageToolbarV2.svelte';
  import { tabMemory } from '#lib/stores/tab-memory.svelte.ts';
  import { formatDateTime } from '#lib/formatters.ts';

  tabMemory.setTitle(page.url, 'Catalog admin');

  let status = $state<CatalogStatusDto | null>(null);
  let review = $state<CatalogIdentityDecisionDto[]>([]);
  let runs = $state<CatalogBackfillRunDto[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let busy = $state<string | null>(null);

  let ingestInput = $state('');
  let ingestKind = $state<CatalogEntityKind>('Release');
  let withChildren = $state(true);

  let backfillKind = $state<CatalogBackfillKind>('LibraryAlbums');
  let dryRun = $state(true);

  const BACKFILL_KINDS: { id: CatalogBackfillKind; label: string }[] = [
    { id: 'LibraryAlbums', label: 'Library albums' },
    { id: 'LibraryArtists', label: 'Library artists' },
    { id: 'CatalogResources', label: 'Catalog resources' },
    { id: 'Images', label: 'Images' }
  ];

  async function load() {
    loading = true;
    error = null;
    try {
      const [s, r, b] = await Promise.all([
        fetchCatalogStatus(),
        fetchIdentityReview(),
        fetchBackfillRuns()
      ]);
      status = s;
      review = r;
      runs = b;
    } catch (e) {
      error = e instanceof Error ? e.message : 'Failed to load catalog admin';
    } finally {
      loading = false;
    }
  }

  onMount(() => {
    void load();
  });

  async function act(label: string, run: () => Promise<unknown>) {
    busy = label;
    try {
      await run();
      toast.success(label);
      await load();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : label + ' failed');
    } finally {
      busy = null;
    }
  }

  function doIngest() {
    const parsed = parseDiscogsRef(ingestInput, ingestKind);
    if (!parsed) {
      toast.error('Paste a Discogs URL or id');
      return;
    }
    void act('Ingest queued', () =>
      ingestCatalogEntity({
        kind: parsed.kind,
        discogsId: parsed.discogsId,
        withChildren
      })
    );
  }

  function subjectOf(d: CatalogIdentityDecisionDto): CatalogIdentitySubject {
    return d.subjectKind === 'LibraryAlbum' ? 'LibraryAlbum' : 'LibraryArtist';
  }

  function targetOf(d: CatalogIdentityDecisionDto): CatalogEntityKind {
    switch (d.targetKind) {
      case 'Artist':
        return 'Artist';
      case 'Label':
        return 'Label';
      case 'Master':
        return 'Master';
      default:
        return 'Release';
    }
  }
</script>

<div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
  <ScrollArea class="min-h-0 flex-1">
    <PageToolbarV2
      title="Catalog admin"
      meta={status
        ? status.enabled
          ? status.authenticated
            ? 'Connected'
            : 'Enabled, not authenticated'
          : 'Disabled'
        : loading
          ? 'Loading…'
          : undefined}
      back={{ label: 'Catalog', href: '/catalog' }}
      grouped
    >
      {#snippet actions()}
        <Button
          variant="ghost"
          size="icon"
          class="size-11 rounded-full md:size-8"
          aria-label="Refresh"
          disabled={loading || busy != null}
          onclick={() => void load()}
        >
          <LoaderCircle class={loading ? 'size-4 animate-spin' : 'size-4'} />
        </Button>
      {/snippet}
    </PageToolbarV2>

    <div class="mx-auto flex w-full max-w-3xl flex-col gap-8 px-4 pb-10 md:px-7">
      {#if loading && !status}
        <div class="flex flex-col gap-3" aria-busy="true">
          <Skeleton class="h-24 w-full rounded-xl" />
          <Skeleton class="h-40 w-full rounded-xl" />
        </div>
      {:else if error && !status}
        <div class="flex flex-col gap-3 py-10" role="alert">
          <p
            class="text-destructive-text text-subheadline inline-flex items-center gap-2 md:text-sm"
          >
            <TriangleAlert class="size-4" />
            {error}
          </p>
          <Button variant="gray" class="w-fit rounded-full" onclick={() => void load()}
            >Retry</Button
          >
        </div>
      {:else if status}
        <section
          class="border-separator rounded-xl border p-4"
          aria-labelledby="catalog-status-heading"
        >
          <h2 id="catalog-status-heading" class="text-title-2 mb-3 md:text-lg">Status</h2>
          <dl class="text-body grid grid-cols-2 gap-x-4 gap-y-2 md:text-sm">
            <dt class="text-muted-foreground">Artists</dt>
            <dd>{status.artists.toLocaleString()}</dd>
            <dt class="text-muted-foreground">Labels</dt>
            <dd>{status.labels.toLocaleString()}</dd>
            <dt class="text-muted-foreground">Masters</dt>
            <dd>{status.masters.toLocaleString()}</dd>
            <dt class="text-muted-foreground">Releases</dt>
            <dd>{status.releases.toLocaleString()}</dd>
            <dt class="text-muted-foreground">Pending images</dt>
            <dd>{status.pendingImages.toLocaleString()}</dd>
            {#if status.rateLimitRemaining != null}
              <dt class="text-muted-foreground">Rate limit left</dt>
              <dd>
                {status.rateLimitRemaining}{#if status.rateLimit != null}/{status.rateLimit}{/if}
              </dd>
            {/if}
          </dl>
        </section>

        <section
          class="border-separator rounded-xl border p-4"
          aria-labelledby="catalog-ingest-heading"
        >
          <h2 id="catalog-ingest-heading" class="text-title-2 mb-3 md:text-lg">Ingest</h2>
          <div class="flex flex-col gap-3">
            <div class="flex flex-col gap-1">
              <Label for="ingest-input">Discogs URL or id</Label>
              <Input
                id="ingest-input"
                bind:value={ingestInput}
                placeholder="https://www.discogs.com/release/…"
                class="h-11 md:h-9"
              />
            </div>
            <div class="flex flex-wrap items-end gap-3">
              <div class="flex flex-col gap-1">
                <Label for="ingest-kind">Kind (for bare ids)</Label>
                <select
                  id="ingest-kind"
                  class="border-input bg-background h-9 rounded-md border px-2 text-sm"
                  bind:value={ingestKind}
                >
                  <option value="Release">Release</option>
                  <option value="Master">Master</option>
                  <option value="Artist">Artist</option>
                  <option value="Label">Label</option>
                </select>
              </div>
              <label class="flex items-center gap-2 text-sm">
                <Switch bind:checked={withChildren} />
                With children
              </label>
              <Button
                variant="tinted"
                class="rounded-full"
                disabled={busy != null}
                onclick={doIngest}
              >
                {#if busy === 'Ingest queued'}<LoaderCircle class="size-4 animate-spin" />{/if}
                Ingest
              </Button>
            </div>
          </div>
        </section>

        <section aria-labelledby="catalog-identity-heading">
          <h2 id="catalog-identity-heading" class="text-title-2 mb-3 md:text-lg">
            Identity review
            <span class="text-muted-foreground text-footnote font-normal md:text-xs">
              ({review.length})
            </span>
          </h2>
          {#if review.length === 0}
            <p class="text-muted-foreground text-subheadline md:text-sm">Nothing waiting.</p>
          {:else}
            <ul class="flex flex-col gap-3">
              {#each review as decision (decision.id)}
                <li class="border-separator rounded-xl border p-3">
                  <div class="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
                    <div class="min-w-0">
                      <p class="text-headline truncate md:text-sm md:font-semibold">
                        {decision.subjectDisplay}
                      </p>
                      <p class="text-muted-foreground text-footnote md:text-xs">
                        → {decision.targetName ?? decision.discogsId}
                        · {decision.targetKind}
                        · {decision.method}
                        · {formatConfidence(decision.confidence)}
                        · {decision.status}
                      </p>
                    </div>
                    <div class="flex shrink-0 gap-2">
                      <Button
                        variant="tinted"
                        size="sm"
                        class="rounded-full"
                        disabled={busy != null}
                        onclick={() =>
                          void act('Chosen', () =>
                            chooseIdentity({
                              subject: subjectOf(decision),
                              subjectDisplay: decision.subjectDisplay,
                              target: targetOf(decision),
                              discogsId: decision.discogsId
                            })
                          )}
                      >
                        Choose
                      </Button>
                      <Button
                        variant="gray"
                        size="sm"
                        class="rounded-full"
                        disabled={busy != null}
                        onclick={() => void act('Rejected', () => rejectIdentity(decision.id))}
                      >
                        Reject
                      </Button>
                    </div>
                  </div>
                </li>
              {/each}
            </ul>
          {/if}
        </section>

        <section
          class="border-separator rounded-xl border p-4"
          aria-labelledby="catalog-backfill-heading"
        >
          <h2 id="catalog-backfill-heading" class="text-title-2 mb-3 md:text-lg">Backfill</h2>
          <div class="mb-4 flex flex-wrap items-end gap-3">
            <div class="flex flex-col gap-1">
              <Label for="backfill-kind">Kind</Label>
              <select
                id="backfill-kind"
                class="border-input bg-background h-9 rounded-md border px-2 text-sm"
                bind:value={backfillKind}
              >
                {#each BACKFILL_KINDS as k (k.id)}
                  <option value={k.id}>{k.label}</option>
                {/each}
              </select>
            </div>
            <label class="flex items-center gap-2 text-sm">
              <Switch bind:checked={dryRun} />
              Dry run
            </label>
            <Button
              variant="tinted"
              class="rounded-full"
              disabled={busy != null}
              onclick={() =>
                void act('Backfill started', () => startBackfill({ kind: backfillKind, dryRun }))}
            >
              Start
            </Button>
          </div>

          <h3 class="text-headline mb-2 md:text-sm md:font-semibold">Runs</h3>
          {#if runs.length === 0}
            <p class="text-muted-foreground text-subheadline md:text-sm">No runs yet.</p>
          {:else}
            <div class="overflow-x-auto overflow-y-hidden" data-scroll-x>
              <table class="text-footnote w-full min-w-[40rem] text-left md:text-xs">
                <thead>
                  <tr class="text-muted-foreground border-separator border-b">
                    <th class="py-2 pr-2 font-medium">Id</th>
                    <th class="py-2 pr-2 font-medium">Kind</th>
                    <th class="py-2 pr-2 font-medium">Status</th>
                    <th class="py-2 pr-2 font-medium">Dry</th>
                    <th class="py-2 pr-2 font-medium">Processed</th>
                    <th class="py-2 pr-2 font-medium">Linked</th>
                    <th class="py-2 pr-2 font-medium">Started</th>
                    <th class="py-2 font-medium">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {#each runs as run (run.id)}
                    <tr class="border-separator border-b align-top">
                      <td class="py-2 pr-2 tabular-nums">{run.id}</td>
                      <td class="py-2 pr-2">{run.kind}</td>
                      <td class="py-2 pr-2">
                        {run.status}
                        {#if run.lastError}
                          <span class="text-destructive-text block max-w-40 truncate"
                            >{run.lastError}</span
                          >
                        {/if}
                      </td>
                      <td class="py-2 pr-2">{run.dryRun ? 'yes' : 'no'}</td>
                      <td class="py-2 pr-2 tabular-nums">{run.processed}</td>
                      <td class="py-2 pr-2 tabular-nums">{run.linked}</td>
                      <td class="py-2 pr-2 whitespace-nowrap">{formatDateTime(run.startedAtUtc)}</td
                      >
                      <td class="py-2">
                        <div class="flex flex-wrap gap-1">
                          <Button
                            variant="gray"
                            size="sm"
                            class="h-8 rounded-full px-2"
                            disabled={busy != null}
                            onclick={() =>
                              void act(`Batch ${run.id}`, () => runBackfillBatch(run.id))}
                          >
                            Batch
                          </Button>
                          <Button
                            variant="ghost"
                            size="sm"
                            class="text-destructive-text h-8 rounded-full px-2"
                            disabled={busy != null}
                            onclick={() =>
                              void act(`Rollback ${run.id}`, () => rollbackBackfill(run.id))}
                          >
                            Rollback
                          </Button>
                        </div>
                      </td>
                    </tr>
                  {/each}
                </tbody>
              </table>
            </div>
          {/if}
        </section>
      {/if}
    </div>
  </ScrollArea>
</div>
