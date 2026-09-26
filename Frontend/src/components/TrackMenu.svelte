<script lang="ts">
	import { ArrowDownTray, ClipboardDocument, EllipsisHorizontal, Icon } from 'svelte-hero-icons';
	import { resolve } from '$app/paths';
	import { heroArtist } from '$lib';
	import { closeOnBack } from '$lib/backWatcher.svelte';
	import { dismiss } from '$lib/dismiss';
	import type { SearchResult } from '$states/search.svelte';
	import queue from '$states/queue.svelte';

	// A row's "…" menu. Bindable so a swipe on the row can open it without the trigger.
	let {
		result,
		open = $bindable(false),
		class: className = ''
	}: { result: SearchResult; open?: boolean; class?: string } = $props();

	// The menu has room for one artist, so it takes the first of a joined credit — the rest are
	// each their own link on the row itself.
	let artistUrl = $derived(
		`${resolve('/artist')}?term=${encodeURIComponent(heroArtist(result.artist))}`
	);

	closeOnBack(
		() => open,
		() => (open = false)
	);

	// An open menu over an action that happened somewhere else reads as nothing
	// happening — on a phone the menu covers the foot of the screen.
	function playNext() {
		queue.playNext(result);
		open = false;
	}

	let copied = $state(false);
	let settle: ReturnType<typeof setTimeout>;

	async function copyId() {
		await navigator.clipboard.writeText(result.id);
		// nothing about a copy is visible anywhere else, so the menu stays open
		// long enough to say so and then takes itself away
		copied = true;
		clearTimeout(settle);
		settle = setTimeout(() => {
			copied = false;
			open = false;
		}, 900);
	}
</script>

<details class="relative {className}" bind:open {@attach open && dismiss(() => (open = false))}>
	<summary
		aria-label="More actions for {result.name}"
		class="flex size-11 list-none items-center justify-center rounded-[5px] border border-haze text-fog hover:bg-surface-200 hover:text-chalk focus-visible:outline-2 focus-visible:outline-primary-200 sm:size-7 [&::-webkit-details-marker]:hidden"
	>
		<Icon src={EllipsisHorizontal} mini size="16" />
	</summary>
	<!-- A phone gets the menu as a sheet at the foot of the screen: under the thumb,
	     full-width targets, and never clipped by the list scrolling past the last row.
	     The backdrop is the way out a tap expects; back and Escape work too.

	     Both take focus themselves (tabindex -1). A press on something that cannot
	     be focused hands the focus to the nearest ancestor that can, and here that is
	     the row: `dismiss` would see the focus leave, close the menu on the way down,
	     and the release would land on the row underneath and play it. -->
	<div
		class="fixed inset-0 z-40 bg-dark-0/60 sm:hidden"
		aria-hidden="true"
		tabindex="-1"
		onclick={() => (open = false)}
	></div>
	<div
		tabindex="-1"
		class="outline-none fixed inset-x-2 bottom-2 z-50 grid gap-0.5 rounded-panel border border-haze bg-surface-100 p-1.5 text-left text-sm sm:absolute sm:inset-x-auto sm:bottom-auto sm:right-0 sm:z-20 sm:mt-1 sm:w-44 sm:p-1 sm:text-xs"
	>
		<p class="truncate px-2 pb-1.5 pt-1 font-medium text-chalk sm:hidden">{result.name}</p>
		<button
			type="button"
			class="flex min-h-12 items-center rounded-art px-2 text-left hover:bg-surface-200 sm:min-h-10"
			onclick={playNext}
		>
			Play next
		</button>
		<!-- room queue items carry no contentUrl; hide the action rather than
		     linking nowhere -->
		{#if result.contentUrl}
			<a
				href={result.contentUrl}
				download
				class="flex min-h-12 items-center gap-2 rounded-art px-2 hover:bg-surface-200 sm:min-h-10"
			>
				<Icon src={ArrowDownTray} mini size="14" /> Download raw
			</a>
		{/if}
		<button
			type="button"
			class="flex min-h-12 items-center gap-2 rounded-art px-2 text-left hover:bg-surface-200 sm:min-h-10"
			onclick={copyId}
		>
			<Icon src={ClipboardDocument} mini size="14" />
			{copied ? 'Copied' : 'Copy id'}
		</button>
		<a
			href={artistUrl}
			class="flex min-h-12 items-center rounded-art px-2 hover:bg-surface-200 sm:min-h-10"
		>
			Go to artist
		</a>
	</div>
</details>
