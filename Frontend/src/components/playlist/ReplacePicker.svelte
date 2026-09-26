<script lang="ts">
	import { untrack } from 'svelte';
	import { ChevronLeft, Icon, MagnifyingGlass } from 'svelte-hero-icons';
	import { convertTimeSpanStringToSeconds, getTimeString, heroArtist, sourceOf } from '$lib';
	import { streamSearch } from '$requests/search';
	import type { SearchResult } from '$states/search.svelte';
	import Skeleton from '$components/Skeleton.svelte';

	// TrackMenu's "Replace…": the menu's actions give way to this, under the lifted track. It
	// starts on the track's own name, so the first answers are the same song from the library,
	// Deezer and YouTube — and the field takes anything, so the row can become another song.
	// `item`, `size` and `list` are the lift's own, so these rows are the menu's rows.
	let {
		track,
		pick,
		back,
		item,
		size,
		list
	}: {
		track: SearchResult;
		pick: (result: SearchResult) => void;
		back: () => void;
		item: string;
		size: string;
		list: string;
	} = $props();

	// drawn fresh each time the view opens, so the first term is the track's for good
	let draft = $state(untrack(() => `${heroArtist(track.artist)} ${track.name}`));
	let term = $state(untrack(() => draft));
	let results = $state<SearchResult[]>([]);
	let searching = $state(true);
	let failed = $state(false);

	// the search page's loop: a term abandoned mid-stream keeps arriving, and `live` drops it
	$effect(() => {
		const stream = streamSearch(term, fetch);
		results = [];
		searching = true;
		failed = false;

		let live = true;
		(async () => {
			try {
				for await (const result of stream) {
					if (!live) return;
					results.push(result);
				}
			} catch {
				if (live) failed = true;
			} finally {
				if (live) searching = false;
			}
		})();

		return () => (live = false);
	});

	function search(event: SubmitEvent) {
		event.preventDefault();
		if (draft.trim()) term = draft.trim();
	}

	// the pressed "Replace…" is gone once the view swaps, so the focus goes somewhere real —
	// not the field, which would put a phone's keyboard over the answers
	const focus = (node: HTMLElement) => node.focus();
</script>

<button type="button" class={item} onclick={back} {@attach focus}>
	<Icon src={ChevronLeft} mini {size} class="shrink-0 text-fog" /> Replace
</button>

<!-- the manual search: another version, or another song entirely -->
<form class="flex gap-2 p-2" onsubmit={search}>
	<!-- select-text: iOS will not type into a field under the lift's select-none -->
	<input
		type="search"
		bind:value={draft}
		aria-label="Search for a replacement"
		class="min-w-0 flex-1 select-text rounded-row border border-haze bg-dark-0 px-2.5 py-1.5 text-sm text-chalk focus:border-primary-0 focus:ring-primary-0"
	/>
	<button
		type="submit"
		class="flex min-h-10 shrink-0 items-center gap-1.5 rounded-row border border-haze px-3 text-sm font-semibold hover:bg-surface-200"
	>
		<Icon src={MagnifyingGlass} mini size="14" /> Search
	</button>
</form>

<!-- scrolls on its own; the names truncate to the box rather than widen it past the track.
     A fixed height, not a cap: the answers stream in while the step into this view is still
     running, and a box that grew under it would be squeezed into its first size — and
     after it, the lift would re-centre with every answer. -->
<div
	class="grid h-[min(20rem,45dvh)] grid-cols-1 content-start overflow-y-auto overscroll-contain contain-inline-size {list}"
	aria-busy={searching}
>
	{#each results as result (result.id)}
		{@const source = sourceOf(result.id)}
		{@const current = result.id === track.id}
		<button type="button" class="{item} disabled:hover:bg-transparent" disabled={current} onclick={() => pick(result)}>
			<img
				src={result.thumbnailUrl ?? '/empty.png'}
				alt=""
				class="size-9 shrink-0 rounded-art object-cover"
			/>
			<span class="min-w-0 flex-1">
				<span class="block truncate text-sm">{result.name}</span>
				<span class="block truncate text-xs text-fog">{result.artist}</span>
			</span>
			<!-- the badge is the point of the list: which copy this one is -->
			<span
				class="shrink-0 rounded-full px-1.5 py-px font-mono text-[0.6rem] tracking-[0.09em] {source.badge}"
				>{source.name}</span
			>
			<span class="min-w-10 shrink-0 text-right font-mono text-[0.68rem] text-fog">
				{current ? 'current' : getTimeString(convertTimeSpanStringToSeconds(result.duration))}
			</span>
		</button>
	{/each}

	{#if searching && results.length < 3}
		{#each [...Array(3 - results.length).keys()] as row (row)}
			<div class="{item} pointer-events-none">
				<Skeleton class="size-9" />
				<Skeleton class="h-3 w-2/5" />
			</div>
		{/each}
	{:else if failed}
		<p class="px-4 py-3 text-sm text-ember">The search did not finish. Search again.</p>
	{:else if !searching && results.length === 0}
		<p class="px-4 py-3 text-sm text-fog">Nothing matched “{term}”. Try other words.</p>
	{/if}
</div>
<p class="sr-only" aria-live="polite">
	{searching ? '' : `${results.length} results for ${term}`}
</p>
