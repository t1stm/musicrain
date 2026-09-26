<script lang="ts">
	import { untrack } from 'svelte';
	import { resolve } from '$app/paths';
	import type { PageData } from './$types';
	import type { SearchResult } from '$states/search.svelte';
	import ArtistLink from '$components/ArtistLink.svelte';
	import RowSkeleton from '$components/RowSkeleton.svelte';
	import { convertTimeSpanStringToSeconds, getTimeString, pressKeys } from '$lib';
	import queue from '$states/queue.svelte';

	const { data }: { data: PageData } = $props();

	let tracks = $state<SearchResult[]>([]);
	// Only the first paint, before the effect below runs: the effect owns this from then on.
	let loading = $state(untrack(() => Boolean(data.tracks)));

	let length = $derived(
		getTimeString(
			tracks.reduce((total, track) => total + convertTimeSpanStringToSeconds(track.duration), 0)
		)
	);
	let artistUrl = $derived(`${resolve('/artist')}?term=${encodeURIComponent(data.artist)}`);

	// Takes a push rather than the list itself, so the effect below never reads the state it just
	// reset — a synchronous read of that would make the effect its own dependency.
	async function fill(stream: AsyncIterable<SearchResult>, alive: () => boolean) {
		for await (const track of stream) {
			if (!alive()) return;
			tracks.push(track);
		}
	}

	// An effect rather than onMount: opening another album is a navigation to this same route, so this
	// component is reused and only `data` changes. onMount would fire once and leave the previous
	// album's rows on screen under the new album's name.
	$effect(() => {
		const stream = data.tracks;

		tracks = [];
		loading = Boolean(stream);
		if (!stream) return;

		// An album abandoned mid-stream keeps arriving; `live` is what stops those tracks being pushed
		// into the list the next album is filling.
		let live = true;

		fill(stream, () => live)
			.catch(() => {})
			.finally(() => {
				if (live) loading = false;
			});

		return () => (live = false);
	});
</script>

<svelte:head><title>{data.album ? `${data.album} · musicrain` : 'Album · musicrain'}</title></svelte:head>

<div class="page gap-6 pb-6 sm:gap-8 sm:pb-28">
	{#if !data.album || !data.artist}
		<div class="p-4 sm:p-6">
			<p class="eyebrow text-gold">Album</p>
			<h1 class="mt-2 font-display text-lg font-light tracking-tight sm:text-2xl">Choose an album</h1>
			<p class="mt-2 max-w-lg text-fog">Album names on a track's line open the record they belong to.</p>
		</div>
	{:else}
		<!-- The hero is the playlist page's `#player-cover` treatment: the cover, full-bleed, 74% dark.
		     The blur is dropped below sm: — it costs more than it gives on a phone. -->
		<header class="relative isolate overflow-hidden">
			<img
				src={tracks[0]?.thumbnailUrl ?? '/empty.png'}
				alt=""
				class="absolute inset-0 -z-10 size-full object-cover"
			/>
			<span class="absolute inset-0 -z-10 bg-dark-0/[0.74] sm:backdrop-blur-[2px]"></span>

			<div class="flex flex-col gap-2 p-4 sm:p-8">
				<p class="eyebrow text-gold">Album</p>
				<h1 class="font-display text-xl font-extralight tracking-tight sm:text-3xl">{data.album}</h1>

				<p class="font-mono text-[0.68rem] uppercase tracking-[0.13em] text-fog">
					<ArtistLink artist={data.artist} /> · {loading ? '…' : tracks.length}
					{tracks.length === 1 && !loading ? 'track' : 'tracks'}{loading ? '' : ` · ${length}`}
				</p>

				<div class="mt-2 flex flex-wrap items-center gap-2">
					<button
						type="button"
						class="min-h-9 rounded-row bg-primary-600 px-3 py-1.5 text-sm font-semibold text-white hover:bg-primary-0 disabled:opacity-60"
						disabled={tracks.length === 0}
						onclick={() => queue.replaceWith(tracks)}
					>
						Play all
					</button>
					<button
						type="button"
						class="min-h-9 rounded-row border border-haze px-3 py-1.5 text-sm font-semibold hover:bg-surface-200 disabled:opacity-60"
						disabled={tracks.length === 0}
						onclick={() => tracks.forEach((track) => queue.add(track))}
					>
						Queue all
					</button>
				</div>
			</div>
		</header>

		<section class="px-2 sm:px-8">
			{#if tracks.length > 0}
				<div class="flex flex-col" aria-busy={loading}>
					{#each tracks as track, index (track.id + index)}
						<!-- one press plays, as a search row does: a finger has no double-click -->
						<div
							role="button"
							tabindex="0"
							class="group flex cursor-pointer items-center gap-3 rounded-row px-2 py-2 hover:bg-surface-100 active:bg-surface-200 focus-visible:bg-surface-100 focus-visible:outline-none"
							onclick={(event) => {
								if (!(event.target as HTMLElement).closest('a')) queue.playNow(track);
							}}
							onkeydown={pressKeys(() => queue.playNow(track))}
						>
							<span class="w-6 shrink-0 text-right font-mono text-[0.68rem] text-fog">{index + 1}</span>
							<img
								src={track.thumbnailUrl ?? '/empty.png'}
								alt=""
								class="size-10 shrink-0 rounded-art object-cover"
							/>
							<div class="min-w-0 flex-1">
								<p class="truncate text-sm">{track.name}</p>
								<p class="truncate text-xs text-fog"><ArtistLink artist={track.artist} /></p>
							</div>
							<span class="shrink-0 font-mono text-[0.68rem] text-fog">
								{getTimeString(convertTimeSpanStringToSeconds(track.duration))}
							</span>
						</div>
					{/each}
				</div>
			{:else if loading}
				<!-- The library answers at once, but a record it has no playlist file for falls through to
				     Deezer — two round trips before the first row. The hero is already up; these fill the
				     rest rather than leaving it looking answered and empty. -->
				<div aria-busy="true">
					{#each [0, 1, 2, 3] as row (row)}
						<RowSkeleton />
					{/each}
				</div>
				<p class="sr-only" aria-live="polite">Loading this album.</p>
			{:else}
				<p class="max-w-lg p-2 text-fog">
					Neither the library nor Deezer has this album. <a
						class="text-primary-500 underline-offset-4 hover:underline"
						href={artistUrl}>Back to {data.artist}</a
					>.
				</p>
			{/if}
		</section>
	{/if}
</div>
