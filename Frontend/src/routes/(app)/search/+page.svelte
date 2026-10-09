<script lang="ts">
	import { type PageData } from './$types';
	import type { SearchResult } from '$states/search.svelte';
	import SearchRow from '$components/search/SearchRow.svelte';
	import RowSkeleton from '$components/RowSkeleton.svelte';
	import { sourceOf, type SourceName } from '$lib/source';

	const { data }: { data: PageData } = $props();

	let results = $state<SearchResult[]>([]);
	let searching = $state(true);
	// A result's section is only known once it is here, so the ones still coming wait
	// in a block of their own below rather than in a section that may not be theirs.
	let waiting = $derived(searching ? Math.max(0, 8 - results.length) : 0);

	// Grouped by where each row came from rather than by "library or not": everything that was not
	// the library used to land under the YouTube heading, which stopped being true once Deezer
	// answered searches too. The order is the order the sections are worth reading in — a local copy
	// beats a Deezer stream beats somebody's upload — and a section with nothing in it is not drawn.
	const sections: { source: SourceName; heading: string; rule: string }[] = [
		{ source: 'Local', heading: 'In the library', rule: 'text-gold' },
		{ source: 'Deezer', heading: 'From Deezer', rule: 'text-deezer' },
		{ source: 'YouTube', heading: 'From YouTube', rule: '' },
		{ source: 'Unknown', heading: 'From somewhere else', rule: '' }
	];

	let grouped = $derived(
		sections
			.map((section) => ({
				...section,
				rows: results.filter((result) => sourceOf(result.id).name === section.source)
			}))
			.filter((section) => section.rows.length > 0)
	);

	// An effect rather than onMount: searching again from the header is a navigation to this same
	// route, so this component is reused and only `data` changes. onMount would fire once and leave
	// the previous term's rows on screen under the new term's heading.
	$effect(() => {
		const stream = data.results;
		results = [];
		searching = Boolean(stream);
		if (!stream) return;

		// A term abandoned mid-stream keeps arriving; `live` is what stops its results being pushed
		// into the list the next term is filling.
		let live = true;
		(async () => {
			try {
				for await (const result of stream) {
					if (!live) return;
					results.push(result);
				}
			} finally {
				if (live) searching = false;
			}
		})();

		return () => (live = false);
	});
</script>

<svelte:head><title>{data.term ? `${data.term} · musicrain` : 'Search · musicrain'}</title></svelte:head>

<div class="page page-column gap-6 p-4 sm:gap-9 sm:p-6 sm:pb-28">
	<h1 class="font-display text-lg font-light leading-tight tracking-tight text-chalk sm:text-2xl">
		{#if searching}
			Searching for “{data.term}”…
		{:else}
			{results.length} results for “{data.term}”
		{/if}
	</h1>

	{#if !searching && results.length === 0}
		<p class="max-w-lg text-fog">
			Nothing matched <strong class="font-semibold text-chalk">{data.term}</strong>. Try an artist
			name, or paste a YouTube link.
		</p>
	{:else}
		{#each grouped as section (section.source)}
			<section class="flex flex-col gap-2">
				<h2 class="eyebrow flex items-center gap-3 {section.rule}">
					{section.heading} · {section.rows.length}
					<span
						class="h-px flex-1 {section.rule ? 'bg-current opacity-35' : 'bg-haze'}"
					></span>
				</h2>
				<div class="flex flex-col">
					{#each section.rows as result (result.id)}
						<SearchRow {result} origin={{ kind: 'search' }} />
					{/each}
				</div>
			</section>
		{/each}

		{#if waiting > 0}
			<div class="flex flex-col" aria-busy="true">
				{#each [...Array(waiting).keys()] as row (row)}
					<RowSkeleton />
				{/each}
			</div>
			<p class="sr-only" aria-live="polite">Searching.</p>
		{/if}
	{/if}
</div>
