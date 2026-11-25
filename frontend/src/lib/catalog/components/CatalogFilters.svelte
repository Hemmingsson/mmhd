<script lang="ts">
  import FilterChip from '#lib/components/v2/FilterChip.svelte';
  import { Input } from '#lib/components/ui/input/index.ts';
  import { Label } from '#lib/components/ui/label/index.ts';
  import type { CatalogFacetsDto } from '#lib/catalog/types.ts';

  export type CatalogFilterState = {
    style: string | null;
    format: string | null;
    country: string | null;
    yearFrom: number | null;
    yearTo: number | null;
    owned: boolean | null;
    sort: string;
  };

  type Props = {
    facets: CatalogFacetsDto | null;
    filters: CatalogFilterState;
    onchange: (next: CatalogFilterState) => void;
    /** When false, hide release-only filters (owned / sort / year). */
    releaseFilters?: boolean;
  };
  const { facets, filters, onchange, releaseFilters = true }: Props = $props();

  const styles = $derived(facets?.styles.slice(0, 24) ?? []);
  const formats = $derived(facets?.formats.slice(0, 16) ?? []);
  const countries = $derived(facets?.countries.slice(0, 16) ?? []);

  function patch(partial: Partial<CatalogFilterState>) {
    onchange({ ...filters, ...partial });
  }

  function toggleStyle(name: string) {
    patch({ style: filters.style === name ? null : name });
  }
  function toggleFormat(value: string) {
    patch({ format: filters.format === value ? null : value });
  }
  function toggleCountry(value: string) {
    patch({ country: filters.country === value ? null : value });
  }
  function toggleOwned(value: boolean) {
    patch({ owned: filters.owned === value ? null : value });
  }
</script>

<div class="flex flex-col gap-3">
  {#if releaseFilters}
    <div class="flex flex-wrap gap-2" role="group" aria-label="Ownership">
      <FilterChip pressed={filters.owned === true} onclick={() => toggleOwned(true)}
        >Owned</FilterChip
      >
      <FilterChip pressed={filters.owned === false} onclick={() => toggleOwned(false)}
        >Unowned</FilterChip
      >
    </div>

    <div class="flex flex-wrap items-end gap-3">
      <div class="flex flex-col gap-1">
        <Label for="catalog-year-from" class="text-muted-foreground text-footnote md:text-xs"
          >Year from</Label
        >
        <Input
          id="catalog-year-from"
          type="number"
          inputmode="numeric"
          class="h-9 w-24"
          value={filters.yearFrom ?? ''}
          min={facets?.minYear ?? 1900}
          max={facets?.maxYear ?? 2100}
          oninput={(e) => {
            const v = (e.currentTarget as HTMLInputElement).value;
            patch({ yearFrom: v ? Number(v) : null });
          }}
        />
      </div>
      <div class="flex flex-col gap-1">
        <Label for="catalog-year-to" class="text-muted-foreground text-footnote md:text-xs"
          >Year to</Label
        >
        <Input
          id="catalog-year-to"
          type="number"
          inputmode="numeric"
          class="h-9 w-24"
          value={filters.yearTo ?? ''}
          min={facets?.minYear ?? 1900}
          max={facets?.maxYear ?? 2100}
          oninput={(e) => {
            const v = (e.currentTarget as HTMLInputElement).value;
            patch({ yearTo: v ? Number(v) : null });
          }}
        />
      </div>
      <div class="flex flex-col gap-1">
        <Label for="catalog-sort" class="text-muted-foreground text-footnote md:text-xs">Sort</Label
        >
        <select
          id="catalog-sort"
          class="border-input bg-background h-9 rounded-md border px-2 text-sm"
          value={filters.sort}
          onchange={(e) => patch({ sort: (e.currentTarget as HTMLSelectElement).value })}
        >
          <option value="year">Year (oldest)</option>
          <option value="-year">Year (newest)</option>
          <option value="title">Title</option>
          <option value="added">Recently added</option>
        </select>
      </div>
    </div>
  {/if}

  {#if styles.length > 0}
    <div>
      <p class="text-muted-foreground text-footnote mb-1.5 md:text-xs">Styles</p>
      <div class="flex flex-wrap gap-2" role="group" aria-label="Styles">
        {#each styles as s (s.name)}
          <FilterChip
            pressed={filters.style === s.name}
            count={s.releaseCount}
            onclick={() => toggleStyle(s.name)}>{s.name}</FilterChip
          >
        {/each}
      </div>
    </div>
  {/if}

  {#if formats.length > 0}
    <div>
      <p class="text-muted-foreground text-footnote mb-1.5 md:text-xs">Formats</p>
      <div class="flex flex-wrap gap-2" role="group" aria-label="Formats">
        {#each formats as f (f.value)}
          <FilterChip
            pressed={filters.format === f.value}
            count={f.count}
            onclick={() => toggleFormat(f.value)}>{f.value}</FilterChip
          >
        {/each}
      </div>
    </div>
  {/if}

  {#if countries.length > 0}
    <div>
      <p class="text-muted-foreground text-footnote mb-1.5 md:text-xs">Countries</p>
      <div class="flex flex-wrap gap-2" role="group" aria-label="Countries">
        {#each countries as c (c.value)}
          <FilterChip
            pressed={filters.country === c.value}
            count={c.count}
            onclick={() => toggleCountry(c.value)}>{c.value}</FilterChip
          >
        {/each}
      </div>
    </div>
  {/if}
</div>
