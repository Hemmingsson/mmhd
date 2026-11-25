<script lang="ts">
  import { page } from '$app/state';
  import { songsStore } from '#lib/stores/songs.svelte.ts';
  import { groupByLabel } from '#lib/library/labels.ts';
  import PageToolbarV2 from '#lib/components/v2/PageToolbarV2.svelte';
  import LabelPage from './LabelPage.svelte';

  const requested = $derived(page.url.searchParams.get('name')?.trim());
  const groups = $derived(groupByLabel(songsStore.albums));
  $effect(() => {
    songsStore.revalidate();
    songsStore.startLive();
    return () => songsStore.stopLive();
  });
</script>

<div class="h-full overflow-y-auto">
  <PageToolbarV2 title={requested || 'Labels'} />
  <LabelPage
    {groups}
    {requested}
    loading={songsStore.isLoading}
    error={songsStore.error}
    retry={() => songsStore.revalidate()}
    portraits={{
      'kess kill': '/label-images/kess-kill.jpg',
      antinote: '/label-images/antinote.jpg',
      'pacific rhythm': '/label-images/pacific-rhythm.jpg'
    }}
  />
</div>
