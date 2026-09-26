<script lang="ts">
	import { untrack } from 'svelte';
	import { Icon, MagnifyingGlass, XMark } from 'svelte-hero-icons';
	import { convertTimeSpanStringToSeconds, getTimeString, heroArtist, sourceOf } from '$lib';
	import { streamSearch } from '$requests/search';
	import type { SearchResult } from '$states/search.svelte';
	import Skeleton from '$components/Skeleton.svelte';

	// Opens under a playlist row and swaps what is in it. It starts on the track's own name, so
	// the first answers are the same song from the library, Deezer and YouTube — and the field
	// takes anything, so the row can become another song outright.
	let {
		track,
		pick,
		close
	}: { track: SearchResult; pick: (result: SearchResult) => void; close: () => void } = $props();

	// the tray is drawn fresh for each row it opens under, so the first term is the track's for good
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

	// on a phone the tray opens as the lift lands, maybe under the fold
	const reveal = (node: HTMLElement) => node.scrollIntoView({ block: 'nearest' });
</script>

<div
	class="mx-1 mb-2 grid gap-2 rounded-panel border border-haze bg-surface-100 p-2 sm:ml-11"
	{@attach reveal}
>
	<div class="flex items-center justify-between gap-2 pl-1">
		<p class="eyebrow truncate">Replace “{track.name}” with</p>
		<button
			type="button"
			aria-label="Close replace"
			class="flex size-9 shrink-0 items-center justify-center rounded-art text-fog hover:bg-surface-200 hover:text-chalk sm:size-7"
			onclick={close}
		>
			<Icon src={XMark} mini size="16" />
		</button>
	</div>

	<!-- the manual search: another version, or another song entirely -->
	<form class="flex gap-2" onsubmit={search}>
		<input
			type="search"
			bind:value={draft}
			aria-label="Search for a replacement"
			class="min-w-0 flex-1 rounded-row border border-haze bg-dark-0 px-2.5 py-1.5 text-sm text-chalk focus:border-primary-0 focus:ring-primary-0"
		/>
		<button
			type="submit"
			class="flex min-h-9 shrink-0 items-center gap-1.5 rounded-row border border-haze px-3 text-sm font-semibold hover:bg-surface-200"
		>
			<Icon src={MagnifyingGlass} mini size="14" /> Search
		</button>
	</form>

	<div class="grid max-h-[min(20rem,45dvh)] grid-cols-1 overflow-y-auto overscroll-contain" aria-busy={searching}>
		{#each results as result (result.id)}
			{@const source = sourceOf(result.id)}
			{@const current = result.id === track.id}
			<button
				type="button"
				class="flex min-h-13 items-center gap-3 rounded-row px-1.5 text-left hover:bg-surface-200 active:transform-none active:bg-surface-200 disabled:hover:bg-transparent sm:min-h-11"
				disabled={current}
				onclick={() => pick(result)}
			>
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
				<div class="flex min-h-13 items-center gap-3 px-1.5 sm:min-h-11">
					<Skeleton class="size-9" />
					<Skeleton class="h-3 w-2/5" />
				</div>
			{/each}
		{:else if failed}
			<p class="px-1.5 py-2 text-sm text-ember">The search did not finish. Search again.</p>
		{:else if !searching && results.length === 0}
			<p class="px-1.5 py-2 text-sm text-fog">Nothing matched “{term}”. Try other words.</p>
		{/if}
	</div>
	<p class="sr-only" aria-live="polite">
		{searching ? '' : `${results.length} results for ${term}`}
	</p>
</div>
