<script lang="ts">
	import { QueueList, XMark } from 'svelte-hero-icons';
	import { resolve } from '$app/paths';
	import { convertTimeSpanStringToSeconds, getTimeString, pressKeys } from '$lib';
	import account from '$states/account.svelte';
	import audio from '$states/audio.svelte';
	import current from '$states/current.svelte';
	import playlists, { toSnapshot } from '$states/playlists.svelte';
	import queue from '$states/queue.svelte';
	import type { SearchResult } from '$states/search.svelte';
	import ArtistLink from '$components/ArtistLink.svelte';
	import SwipeRow from '$components/SwipeRow.svelte';
	import { sourceOf } from '$lib/source';
	import { closeOnBack } from '$lib/backWatcher.svelte';
	import { reorder } from '$lib/reorder';


	let items = $derived(queue.items);
	let currentIndex = $derived(queue.currentIndex);
	let currentItem = $derived(items[currentIndex]);
	let nextItems = $derived(items.slice(currentIndex + 1));
	let playedItems = $derived(items.slice(0, currentIndex));
	let showPlayed = $state(false);
	let progress = $derived(
		current.lengthSeconds > 0 ? Math.min((audio.currentSeconds / current.lengthSeconds) * 100, 100) : 0
	);
	let remainingSeconds = $derived(
		Math.max(current.lengthSeconds - audio.currentSeconds, 0) +
			nextItems.reduce((total, item) => total + convertTimeSpanStringToSeconds(item.duration), 0)
	);
	let remainingTime = $derived(getTimeString(remainingSeconds));

	function remove(index: number, event: MouseEvent) {
		event.stopPropagation();
		queue.removeIndex(index);
	}

	// By the track, not the index: a swipe's action runs after its answer has shown, and
	// the queue can move in that time. ponytail: a room's rebroadcast in those 600ms
	// hands out new objects and the swipe does nothing — match by id if that bites.
	function playNextSwiped(item: SearchResult) {
		const at = queue.items.indexOf(item);
		if (at !== -1) queue.setNext(at);
	}

	function play(index: number) {
		queue.playIndex(index);
	}

	// see ArtistLink: the artist name is a real link, so the row must not act on it.
	// One press, not two: a double-click is not a thing a finger can reliably do.
	function playUnlessLink(index: number, event: MouseEvent) {
		if ((event.target as HTMLElement).closest('a')) return;
		play(index);
	}

	function imageFallback(event: Event) {
		const image = event.currentTarget as HTMLImageElement;
		if (!image.src.endsWith('/empty.png')) image.src = '/empty.png';
	}

	function itemLabel(item: SearchResult) {
		return `${item.name} by ${item.artist}`;
	}

	// The dock is 380px wide, so saving is not a modal — the footer row becomes the
	// name field and comes back when it is done with.
	let naming = $state(false);
	let draftName = $state('');
	let saved = $state<{ id: string; name: string } | null>(null);

	closeOnBack(
		() => naming,
		() => (naming = false)
	);

	function openSave() {
		saved = null;
		naming = true;
		draftName = `Queue · ${new Date().toLocaleDateString(undefined, { day: 'numeric', month: 'short' })}`;
	}

	async function savePlaylist(event: SubmitEvent) {
		event.preventDefault();
		if (!draftName.trim()) return;

		// In a room the queue is the server's, not yours — saving still works, it
		// snapshots what is playing right now.
		const made = await playlists.save({ name: draftName.trim(), tracks: items.map(toSnapshot) });
		if (!made) return;

		saved = { id: made.id, name: made.name };
		naming = false;
	}
</script>

<!-- the dock owns the panel chrome and the tab strip; this is only its body -->
<section class="flex min-h-0 flex-1 flex-col overflow-hidden">
{#if items.length === 0}
		<!-- nothing here scrolls either, so all of it is the sheet's handle too -->
		<div data-sheet-handle class="flex-1 max-sm:touch-none">
			<p class="mt-4 max-w-64 text-sm text-fog">
				Queue’s empty. Add something from <a class="text-primary-500 underline-offset-4 hover:underline" href={resolve('/search')}>search</a>, or
				<a class="text-primary-500 underline-offset-4 hover:underline" href={resolve('/')}>roll a track</a> on the home page.
			</p>
		</div>
	{:else}
	<!-- nothing here scrolls, so on a phone it is more of the sheet's handle than the grip is -->
	<section data-sheet-handle class="py-3 max-sm:touch-none">
		<h3 class="eyebrow mb-2">Now playing</h3>
		{#if currentItem}
			<div class="flex items-center gap-3">
				<img src={currentItem.thumbnailUrl ?? '/empty.png'} alt="" class="size-14 rounded-art object-cover" onerror={imageFallback} />
				<div class="min-w-0 flex-1">
					<p class="truncate text-sm font-semibold">{currentItem.name}</p>
					<p class="truncate text-xs text-fog">
						<ArtistLink artist={currentItem.artist} /> · {sourceOf(currentItem.id).name}
					</p>
					<div class="mt-2 h-px overflow-hidden bg-haze">
						<div class="h-full bg-primary-500" style:width={progress + '%'}></div>
					</div>
				</div>
			</div>
		{:else}
			<p class="text-sm text-fog">Pick a track to start listening.</p>
		{/if}
	</section>

	<section class="flex min-h-0 flex-1 flex-col border-t border-haze py-3">
		<h3 class="eyebrow mb-2">Next up · {nextItems.length}</h3>
		{#if nextItems.length > 0}
			<!-- The row lands where it was dropped — that is the whole point of dragging it — and
			     past the last row is the end of the queue. -->
			<div class="min-h-0 flex-1 space-y-1 overflow-y-auto pr-1" {@attach reorder((from, to) => queue.move(from, to))}>
				{#each nextItems as item, offset (item.id + offset)}
					{@const index = currentIndex + offset + 1}
					<!-- the number and the sleeve are the reorder's grip, so a swipe starts on the words -->
					<SwipeRow
						ignore="[data-grip]"
						right={{
							label: 'Play next',
							icon: QueueList,
							color: 'var(--color-primary-600)',
							done: 'Next up',
							run: () => playNextSwiped(item)
						}}
						left={{
							label: 'Remove',
							icon: XMark,
							color: 'var(--color-ember)',
							done: 'Removed',
							run: () => queue.removeItem(item)
						}}
					>
						<div
							data-index={index}
							role="button"
							tabindex="0"
							class="group flex cursor-pointer select-none items-center gap-2 rounded-[5px] px-1 py-1.5 transition-colors hover:bg-surface-0 active:bg-surface-200 focus-visible:bg-surface-0 focus-visible:outline-none"
							onclick={(event) => playUnlessLink(index, event)}
							onkeydown={pressKeys(() => play(index))}
						>
							<!-- The number and the sleeve are where a finger picks the row up; the
							     words scroll the list. A mouse can take the row anywhere. -->
							<span data-grip class="flex shrink-0 cursor-grab touch-none items-center gap-2 self-stretch active:cursor-grabbing">
								<span class="w-4 text-right font-mono text-[0.68rem] text-fog">{offset + 1}</span>
								<img src={item.thumbnailUrl ?? '/empty.png'} alt="" draggable="false" class="size-9 rounded-art object-cover" onerror={imageFallback} />
							</span>
							<div class="min-w-0 flex-1">
								<p class="truncate text-sm">{item.name}</p>
								<p class="truncate text-xs text-fog">
									<ArtistLink artist={item.artist} /> · {sourceOf(item.id).name}
								</p>
							</div>
							<button
								type="button"
								aria-label={`Remove ${itemLabel(item)} from queue`}
								class="flex size-9 shrink-0 items-center justify-center rounded-art text-fog hover:bg-surface-200 hover:text-chalk focus-visible:opacity-100 group-hover:opacity-100 group-focus-visible:opacity-100 pointer-fine:opacity-0"
								onclick={(event) => remove(index, event)}
							>
								×
							</button>
						</div>
					</SwipeRow>
				{/each}
			</div>
		{:else}
			<p class="text-sm text-fog">Nothing queued after this track.</p>
		{/if}
	</section>

	<section class="border-t border-haze py-3">
		<button type="button" class="eyebrow flex w-full items-center justify-between text-left hover:text-chalk" onclick={() => (showPlayed = !showPlayed)}>
			<span>Played · {playedItems.length}</span>
			<span aria-hidden="true">{showPlayed ? '−' : '+'}</span>
		</button>
		{#if showPlayed && playedItems.length > 0}
			<ul class="mt-2 max-h-28 space-y-1 overflow-y-auto">
				{#each playedItems as item, index (item.id + index)}
					<li class="flex items-center gap-2 px-1 py-1">
						<span class="w-4 text-right font-mono text-[0.68rem] text-fog">{index + 1}</span>
						<span class="truncate text-xs text-fog">{item.name}</span>
					</li>
				{/each}
			</ul>
		{/if}
	</section>

	{#if playlists.error}
		<p class="pt-2 text-xs text-ember">{playlists.error}</p>
	{/if}

	<footer class="flex items-center gap-2 border-t border-haze pt-3">
		{#if naming && account.signedIn}
			<form class="flex w-full items-center gap-2" onsubmit={savePlaylist}>
				<!-- svelte-ignore a11y_autofocus -->
				<input
					type="text"
					bind:value={draftName}
					maxlength="80"
					autofocus
					aria-label="Playlist name"
					class="rounded-row border border-haze bg-dark-0 min-w-0 flex-1 py-1 text-xs text-chalk ring-primary-0 focus:border-primary-0 focus-visible:ring-2"
				/>
				<button
					type="submit"
					disabled={playlists.loading}
					class="min-h-9 shrink-0 rounded-[5px] bg-primary-600 px-2 py-1 text-xs font-semibold text-white hover:bg-primary-0 disabled:opacity-60"
				>
					{playlists.loading ? 'Saving…' : 'Save playlist'}
				</button>
			</form>
		{:else}
			<span class="mr-auto truncate font-mono text-[0.68rem] uppercase tracking-[0.13em] text-fog">
				{#if naming}
					Sign in to keep playlists.
				{:else if saved}
					Saved to <a class="text-primary-500 underline-offset-4 hover:underline" href={`${resolve('/playlist')}?id=${saved.id}`}>{saved.name}</a>
				{:else}
					{items.length} tracks · {remainingTime} left
				{/if}
			</span>
			<button type="button" class="min-h-9 rounded-[5px] border border-haze px-2 py-1 text-xs font-semibold hover:bg-surface-200" onclick={openSave}>
				Save
			</button>
			<button type="button" class="min-h-9 rounded-[5px] border border-haze px-2 py-1 text-xs font-semibold hover:bg-surface-200" onclick={() => queue.shuffle()}>
				Shuffle
			</button>
			<button type="button" class="min-h-9 rounded-[5px] border border-haze px-2 py-1 text-xs font-semibold text-fog hover:bg-surface-200 hover:text-chalk" onclick={() => queue.clearOthers()}>
				Clear
			</button>
		{/if}
	</footer>
	{/if}
</section>
