<script lang="ts">
	import { ArrowDownTray, ClipboardDocument, EllipsisHorizontal, Icon, Play } from 'svelte-hero-icons';
	import { resolve } from '$app/paths';
	import { convertTimeSpanStringToSeconds, getTimeString, heroArtist, pressKeys, sourceOf } from '$lib';
	import { closeOnBack } from '$lib/backWatcher.svelte';
	import { dismiss } from '$lib/dismiss';
	import type { SearchResult } from '$states/search.svelte';
	import queue from '$states/queue.svelte';
	import session from '$states/session.svelte';
	import ArtistLink from '$components/ArtistLink.svelte';


	const { result }: { result: SearchResult } = $props();
	let duration = $derived(getTimeString(convertTimeSpanStringToSeconds(result.duration)));
	let isLong = $derived(convertTimeSpanStringToSeconds(result.duration) > 15 * 60);
	let source = $derived(sourceOf(result.id));
	// The menu has room for one artist, so it takes the first of a joined credit — the rest are
	// each their own link on the row itself.
	let artistUrl = $derived(
		`${resolve('/artist')}?term=${encodeURIComponent(heroArtist(result.artist))}`
	);
	// Every album tag is a link, including the ones the library holds no playlist file for: the tag
	// says the record exists, and the endpoint decides whether the library or Deezer can list it.
	const albumUrl = (album: string) =>
		`${resolve('/album')}?artist=${encodeURIComponent(heroArtist(result.artist))}&album=${encodeURIComponent(album)}`;

	let menuOpen = $state(false);
	closeOnBack(
		() => menuOpen,
		() => (menuOpen = false)
	);

	function playNow() {
		queue.playNow(result);
	}

	// Anything inside the row that is a link (the artist name, the download) owns
	// its own click. Stopping propagation there instead would hide the click from
	// SvelteKit's router, which listens on document.documentElement — the link
	// would fall back to a full page load and wipe the queue. The menu is the same:
	// a press anywhere in it, the gaps and the phone's backdrop included, is the
	// menu's and never a play.
	function playUnlessLink(event: MouseEvent) {
		if ((event.target as HTMLElement).closest('a, details')) return;
		playNow();
	}

	function handlePlayNow(event: Event) {
		event.stopPropagation();
		playNow();
	}

	function addToQueue(event: Event) {
		event.stopPropagation();
		queue.add(result);
	}

	// An open menu over an action that happened somewhere else reads as nothing
	// happening — on a phone the menu covers the foot of the screen.
	function playNext() {
		queue.playNext(result);
		menuOpen = false;
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
			menuOpen = false;
		}, 900);
	}
</script>

<div
	class="group grid cursor-pointer grid-cols-[2.75rem_minmax(0,1fr)_auto] items-center gap-3 rounded-row px-2 py-2 transition-colors hover:bg-surface-100 active:bg-surface-200 focus-visible:bg-surface-100 focus-visible:outline-none sm:grid-cols-[2.75rem_minmax(0,1fr)_auto_auto] sm:gap-3.5 sm:px-2.5"
	role="button"
	tabindex="0"
	onclick={playUnlessLink}
	onkeydown={pressKeys(playNow)}
>
	<img
		src={result.thumbnailUrl ?? '/empty.png'}
		alt=""
		class="size-11 rounded-art object-cover"
		onerror={(event: Event) => {
			const image = event.currentTarget as HTMLImageElement;
			if (!image.src.endsWith('/empty.png')) image.src = '/empty.png';
		}}
	/>
	<div class="min-w-0">
		<p class="line-clamp-2 text-sm font-medium leading-snug text-chalk">{result.name}</p>
		<p class="truncate text-[0.79rem] text-fog">
			<ArtistLink artist={result.artist} />{#if result.album}&nbsp;·&nbsp;<a
					href={albumUrl(result.album)}
					draggable="false"
					class="rounded-art underline-offset-4 hover:text-chalk hover:underline focus-visible:outline-2 focus-visible:outline-primary-200"
					>{result.album}</a
				>{/if}<!--
			--><span class="font-mono sm:hidden"> · {source.name} · {duration}{isLong ? ' · long' : ''}</span>
		</p>
	</div>

	<!-- its own column is a luxury a 320px row cannot afford; the artist line
	     carries the same two facts instead -->
	<div class="hidden items-center gap-2.5 sm:flex">
		<!-- Solid, unlike the outlined "long" beside it: this one is the row's identity rather than a
		     remark about it, and it is the same fill the home page's cards wear. -->
		<span
			class="rounded-full px-1.5 py-px font-mono text-[0.6rem] tracking-[0.09em] {source.badge}"
			>{source.name}</span
		>
		{#if isLong}
			<span
				class="rounded-full border border-gold/45 px-1.5 py-px font-mono text-[0.6rem] uppercase tracking-[0.09em] text-gold"
				>long</span
			>
		{/if}
		<span class="w-14 text-right font-mono text-[0.79rem] text-fog">{duration}</span>
	</div>

	<div
		class="flex items-center justify-end gap-1.5 sm:pointer-fine:opacity-0 sm:pointer-fine:transition-opacity sm:pointer-fine:group-hover:opacity-100 sm:pointer-fine:group-focus-within:opacity-100 sm:pointer-fine:has-open:opacity-100"
	>
		{#if !session.inRoom}
			<button
				type="button"
				class="hidden items-center gap-1 rounded-[5px] bg-primary-600 px-2.5 py-1 text-xs font-semibold text-white focus-visible:outline-2 focus-visible:outline-primary-200 sm:inline-flex"
				onclick={handlePlayNow}
			>
				<Icon src={Play} mini size="14" /> <span>Play</span>
			</button>
		{/if}
		<button
			type="button"
			aria-label="Add {result.name} to queue"
			class="hidden rounded-[5px] px-2.5 py-1 text-xs font-semibold focus-visible:outline-2 focus-visible:outline-primary-200 sm:block {session.inRoom
				? 'bg-primary-600 text-white'
				: 'border border-haze text-chalk hover:bg-surface-200'}"
			onclick={addToQueue}
		>
			Queue
		</button>
		<details
			class="relative"
			bind:open={menuOpen}
			{@attach menuOpen && dismiss(() => (menuOpen = false))}
		>
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
				onclick={() => (menuOpen = false)}
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
	</div>
</div>
