<script lang="ts">
	import { untrack } from 'svelte';
	import type { PageData } from './$types';
	import type { SearchResult } from '$states/search.svelte';
	import SearchRow from '$components/search/SearchRow.svelte';
	import RowSkeleton from '$components/RowSkeleton.svelte';

	const { data }: { data: PageData } = $props();

	type Side = keyof NonNullable<PageData['streams']>;

	// In the order worth reading them in — a local copy beats a Deezer stream beats somebody's upload —
	// which is also the order the page steps along when a side turns out to have nothing.
	const tabs: { side: Side; long: string; short: string; noun: string }[] = [
		{ side: 'library', long: 'In the library', short: 'Library', noun: 'library' },
		{ side: 'deezer', long: 'From Deezer', short: 'Deezer', noun: 'Deezer' },
		{ side: 'youtube', long: 'From YouTube', short: 'YouTube', noun: 'YouTube' }
	];

	let results = $state<Record<Side, SearchResult[]>>({ library: [], deezer: [], youtube: [] });
	// Only the first paint, before the effect below runs: the effect owns these from then on.
	let loading = $state<Record<Side, boolean>>(
		untrack(() => {
			const on = Boolean(data.streams);
			return { library: on, deezer: on, youtube: on };
		})
	);

	// The library is the tab this page opens on, but an artist the library has never
	// heard of should not open on an empty one. Until a side has answered there is
	// nothing to move away from, and a tab pressed by hand is never moved.
	let activeTab = $state<Side>('library');
	let chosen = false;
	let active = $derived(tabs.find((tab) => tab.side === activeTab)!);
	let activeResults = $derived(results[activeTab]);
	let activeLoading = $derived(loading[activeTab]);
	let waiting = $derived(activeLoading ? Math.max(0, 8 - activeResults.length) : 0);

	function choose(side: Side) {
		chosen = true;
		activeTab = side;
	}

	/** Steps past every side that answered with nothing, stopping at one still arriving. */
	function settle() {
		if (chosen) return;
		let at = tabs.indexOf(active);
		while (at < tabs.length - 1 && !loading[tabs[at].side] && results[tabs[at].side].length === 0) at++;
		activeTab = tabs[at].side;
	}

	// Three tabs overflow a narrow phone, so the strip scrolls — and a tab the page stepped to on its
	// own can be the one hanging off the edge. Horizontal only: scrollIntoView would also drag the
	// page up to the strip. The smoothness comes from the strip's own scroll-behavior.
	let tablist = $state<HTMLElement>();
	$effect(() => {
		const tab = tablist?.querySelector<HTMLElement>(`[data-side="${activeTab}"]`);
		if (tab && tablist) tablist.scrollLeft = tab.offsetLeft - (tablist.clientWidth - tab.offsetWidth) / 2;
	});

	/** A side's count while it is still arriving is not a count yet. */
	function count(side: Side) {
		return loading[side] ? '…' : String(results[side].length);
	}

	// Takes a push rather than the list itself, so the effect below never reads the state it just
	// reset — a synchronous read of that would make the effect its own dependency.
	async function fill(
		stream: AsyncIterable<SearchResult>,
		alive: () => boolean,
		push: (result: SearchResult) => void
	) {
		for await (const result of stream) {
			if (!alive()) return;
			push(result);
		}
	}

	// An effect rather than onMount: opening another artist is a navigation to this same route, so
	// this component is reused and only `data` changes. onMount would fire once and leave the
	// previous artist's rows on screen under the new artist's name.
	$effect(() => {
		const streams = data.streams;

		// Back to what a fresh load of this page would show — including the tab, since the artist
		// whose empty library tab was worth stepping around is no longer the one on screen.
		results = { library: [], deezer: [], youtube: [] };
		loading = { library: Boolean(streams), deezer: Boolean(streams), youtube: Boolean(streams) };
		activeTab = 'library';
		chosen = false;
		if (!streams) return;

		// An artist abandoned mid-stream keeps arriving; `live` is what stops those results being
		// pushed into the lists the next artist is filling.
		let live = true;
		const alive = () => live;

		for (const { side } of tabs)
			fill(streams[side], alive, (result) => results[side].push(result))
				.catch(() => {})
				.finally(() => {
					if (!live) return;
					loading[side] = false;
					settle();
				});

		return () => (live = false);
	});
</script>

<svelte:head><title>{data.term ? `${data.term} · musicrain` : 'Artist · musicrain'}</title></svelte:head>

<div class="page page-column gap-5 p-4 sm:gap-6 sm:p-6 sm:pb-28">
	<div>
		<p class="eyebrow text-gold">Artist</p>
		<h1 class="mt-2 font-display text-xl font-light leading-tight tracking-tight text-chalk sm:text-3xl">{data.term || 'Choose an artist'}</h1>
	</div>

	{#if data.term}
		<div bind:this={tablist} class="relative flex w-full overflow-x-auto rounded-panel border border-haze bg-surface-0 p-1 [scrollbar-width:none] motion-safe:scroll-smooth sm:w-fit [&::-webkit-scrollbar]:hidden" role="tablist" aria-label="Artist source">
			{#each tabs as tab (tab.side)}
				<button type="button" role="tab" data-side={tab.side} aria-selected={activeTab === tab.side} class={`min-h-10 flex-1 whitespace-nowrap rounded-row px-3 py-1.5 text-sm font-semibold text-fog hover:text-chalk sm:flex-none ${activeTab === tab.side ? 'bg-primary-600 text-white' : ''}`} onclick={() => choose(tab.side)}><span class="hidden sm:inline">{tab.long}</span><span class="sm:hidden">{tab.short}</span> · {count(tab.side)}</button>
			{/each}
		</div>

		{#if activeResults.length > 0 || waiting > 0}
			<div class="flex flex-col" aria-busy={activeLoading}>
				{#each activeResults as result (result.id)}
					<SearchRow {result} />
				{/each}
				{#each [...Array(waiting).keys()] as row (row)}
					<RowSkeleton />
				{/each}
			</div>
			{#if activeLoading}<p class="sr-only" aria-live="polite">Loading this artist's tracks.</p>{/if}
		{:else}
			<p class="text-fog">No {active.noun} tracks matched this artist.</p>
		{/if}
	{:else}
		<p class="max-w-lg text-fog">Search for an artist, or choose one from the library on the home page.</p>
	{/if}
</div>
