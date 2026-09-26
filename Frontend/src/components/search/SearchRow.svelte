<script lang="ts">
	import { EllipsisHorizontal, Icon, Play, Plus, QueueList } from 'svelte-hero-icons';
	import { resolve } from '$app/paths';
	import { convertTimeSpanStringToSeconds, getTimeString, heroArtist, pressKeys, sourceOf } from '$lib';
	import type { SearchResult } from '$states/search.svelte';
	import queue from '$states/queue.svelte';
	import session from '$states/session.svelte';
	import ArtistLink from '$components/ArtistLink.svelte';
	import SwipeRow from '$components/SwipeRow.svelte';
	import TrackMenu from '$components/TrackMenu.svelte';


	const { result }: { result: SearchResult } = $props();
	let duration = $derived(getTimeString(convertTimeSpanStringToSeconds(result.duration)));
	let isLong = $derived(convertTimeSpanStringToSeconds(result.duration) > 15 * 60);
	let source = $derived(sourceOf(result.id));
	// Every album tag is a link, including the ones the library holds no playlist file for: the tag
	// says the record exists, and the endpoint decides whether the library or Deezer can list it.
	const albumUrl = (album: string) =>
		`${resolve('/album')}?artist=${encodeURIComponent(heroArtist(result.artist))}&album=${encodeURIComponent(album)}`;

	let menuOpen = $state(false);

	function playNow() {
		queue.playNow(result);
	}

	// Anything inside the row that is a link (the artist name, the download) owns
	// its own click. Stopping propagation there instead would hide the click from
	// SvelteKit's router, which listens on document.documentElement — the link
	// would fall back to a full page load and wipe the queue. The menu is the same:
	// a press on its "…" or anywhere in its dropdown is the menu's and never a play.
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
</script>

<!-- Two steps the same width apart: the queue's end, then the front of it. The phone has no
     Queue button on the row, so this is its way to one. -->
<SwipeRow
	right={[
		{
			label: 'Queue',
			icon: Plus,
			color: 'var(--color-surface-400)',
			done: 'Queued',
			run: () => queue.add(result)
		},
		{
			label: 'Play next',
			icon: QueueList,
			color: 'var(--color-primary-600)',
			done: 'Next up',
			run: () => queue.playNext(result)
		}
	]}
	left={{
		label: 'More',
		icon: EllipsisHorizontal,
		color: 'var(--color-surface-300)',
		run: () => (menuOpen = true)
	}}
>
	<div
		data-preview
		class="group grid cursor-pointer grid-cols-[2.75rem_minmax(0,1fr)_auto] items-center gap-3 rounded-row px-2 py-2 transition-colors not-has-open:hover:bg-surface-100 not-has-open:active:bg-surface-200 focus-visible:bg-surface-100 focus-visible:outline-none sm:grid-cols-[2.75rem_minmax(0,1fr)_auto_auto] sm:gap-3.5 sm:px-2.5"
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
			<TrackMenu {result} bind:open={menuOpen} />
		</div>
	</div>
</SwipeRow>
