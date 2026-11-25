<script lang="ts">
  import type { SVGAttributes } from 'svelte/elements';
  import { cn } from '#lib/utils.ts';

  /**
   * The vinyl-disc mark — the same drawing as static/icon.svg, which is the favicon and (via the
   * PNGs rendered from it) the Home Screen icon. It is the one element carried from the landing
   * nav through sign-in, invite and error screens into the installed app, so a visitor meets the
   * same mark at every door.
   *
   * icon.svg swaps its colours with prefers-color-scheme; this uses the theme tokens instead, so it
   * follows the in-app theme toggle as well as the system setting. Always decorative: the product
   * name is next to it or in the page title.
   */
  let { class: className, ...rest }: { class?: string } & SVGAttributes<SVGSVGElement> = $props();
</script>

<svg
  viewBox="0 0 180 180"
  fill="none"
  aria-hidden="true"
  focusable="false"
  class={cn('block size-7 shrink-0', className)}
  {...rest}
>
  <rect width="180" height="180" rx="32" class="fill-foreground"></rect><g class="fill-background"
    ><path transform="translate(32 32)" d="M0 50V0H10L24 22L38 0H48V50H36V20L24 38L12 20V50Z"
    ></path><path
      transform="translate(100 32)"
      d="M0 50V0H10L24 22L38 0H48V50H36V20L24 38L12 20V50Z"
    ></path><path transform="translate(32 98)" d="M0 0H12V19H36V0H48V50H36V31H12V50H0Z"></path><path
      transform="translate(100 98)"
      fill-rule="evenodd"
      d="M0 0H21C39 0 48 9 48 25S39 50 21 50H0ZM12 12V38H21C31 38 36 34 36 25S31 12 21 12Z"
    ></path></g
  >
</svg>
