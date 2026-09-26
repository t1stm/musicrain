<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { resolve } from '$app/paths';
	import ArtistLink from '$components/ArtistLink.svelte';
	import PlaylistCover from '$components/playlist/PlaylistCover.svelte';
	import { convertTimeSpanStringToSeconds, getTimeString, pressKeys } from '$lib';
	import { getPlaylist, type Playlist } from '$requests/playlists';
	import { closeOnBack } from '$lib/backWatcher.svelte';
	import { reorder } from '$lib/reorder';
	import account from '$states/account.svelte';
	import playlists from '$states/playlists.svelte';
	import queue from '$states/queue.svelte';
	import type { SearchResult } from '$states/search.svelte';

	// No `+page.ts`: the bearer token lives in `localStorage`, so the fetch belongs
	// where the account state already is — the same reason `/room` reads its own id.
	let id = $derived(page.url.searchParams.get('id') ?? '');
	let playlist = $state<Playlist | null>(null);
	let missing = $state(false);
	let renaming = $state(false);
	let confirmingDelete = $state(false);
	let draftName = $state('');

	// Two separate layers: back gets out of the delete confirmation without also
	// throwing away the rename that was open behind it.
	closeOnBack(
		() => renaming,
		() => (renaming = false)
	);
	closeOnBack(
		() => confirmingDelete,
		() => (confirmingDelete = false)
	);

	let tracks = $derived(playlist?.tracks ?? []);
	let mine = $derived(!!playlist && playlist.owner === account.username);
	let length = $derived(getTimeString(convertTimeSpanStringToSeconds(playlist?.duration ?? '00:00:00')));

	$effect(() => {
		// re-runs when a token arrives, which is what turns a 404 on a private
		// playlist into the page its owner is expecting
		const token = account.token;
		if (!id) return;

		let live = true;
		getPlaylist(id, token)
			.then((found) => {
				if (!live) return;
				playlist = found;
				missing = false;
			})
			.catch(() => {
				if (live) missing = true;
			});

		return () => {
			live = false;
		};
	});

	/** Every edit is the same shape: change the list here, then send the list. */
	async function commit(next: SearchResult[]) {
		if (!playlist) return;
		playlist = { ...playlist, tracks: next, trackCount: next.length };
		const saved = await playlists.update(playlist.id, { tracks: next });
		if (saved) playlist = saved;
	}

	function remove(index: number) {
		commit(tracks.filter((_, at) => at !== index));
	}

	function move(from: number, to: number) {
		const next = [...tracks];
		const [moved] = next.splice(from, 1);
		next.splice(to, 0, moved);
		commit(next);
	}

	// see ArtistLink: the artist name is a real link, so the row must not act on it
	function playUnlessLink(track: SearchResult, event: MouseEvent) {
		if (!(event.target as HTMLElement).closest('a')) queue.playNow(track);
	}

	async function rename(event: SubmitEvent) {
		event.preventDefault();
		if (!playlist || !draftName.trim()) return;

		const saved = await playlists.update(playlist.id, { name: draftName.trim() });
		if (saved) playlist = saved;
		renaming = false;
	}

	async function toggleVisibility() {
		if (!playlist) return;

		const saved = await playlists.update(playlist.id, { isPublic: !playlist.isPublic });
		if (saved) playlist = saved;
	}

	/** The cover picker. The upload replaces whatever was there, so there is no remove. */
	async function chooseCover(event: Event) {
		const input = event.currentTarget as HTMLInputElement;
		const file = input.files?.[0];
		if (!file || !playlist) return;

		const coverUrl = await playlists.setCover(playlist.id, file);
		// `updatedUtc` is what busts the week-long cache on the URL Dom just replaced
		if (coverUrl) playlist = { ...playlist, coverUrl, updatedUtc: new Date().toISOString() };
		input.value = '';
	}

	async function remove_() {
		if (!playlist) return;

		await playlists.remove(playlist.id);
		await goto(resolve('/playlists'));
	}
</script>

<svelte:head><title>{playlist ? `${playlist.name} · musicrain` : 'Playlist · musicrain'}</title></svelte:head>

<div class="page gap-6 pb-6 sm:gap-8 sm:pb-28">
	{#if missing}
		<div class="p-4 sm:p-6">
			<p class="eyebrow text-primary-500">Playlist</p>
			<h1 class="mt-2 font-display text-lg font-light tracking-tight sm:text-2xl">Not here</h1>
			<p class="mt-2 max-w-lg text-fog">
				This playlist is private, or it is gone. <a
					class="text-primary-500 underline-offset-4 hover:underline"
					href={resolve('/playlists')}>Back to playlists</a
				>.
			</p>
		</div>
	{:else if playlist}
		<!-- The hero is the `#player-cover` treatment: the cover, full-bleed, 74% dark.
		     The blur is dropped below sm: — it costs more than it gives on a phone. -->
		<header class="relative isolate overflow-hidden">
			<PlaylistCover
				{playlist}
				class="absolute inset-0 -z-10 size-full object-cover"
				alt=""
			/>
			<span class="absolute inset-0 -z-10 bg-dark-0/[0.74] sm:backdrop-blur-[2px]"></span>

			<div class="flex flex-col gap-2 p-4 sm:p-8">
				<p class="eyebrow">{mine ? 'Your playlist' : `${playlist.owner}’s playlist`}</p>

				{#if renaming}
					<form class="flex max-w-md gap-2" onsubmit={rename}>
						<input
							type="text"
							bind:value={draftName}
							maxlength="80"
							aria-label="Playlist name"
							class="rounded-row border border-haze bg-dark-0 w-full text-chalk ring-primary-0 focus:border-primary-0 focus-visible:ring-2"
						/>
						<button
							type="submit"
							class="shrink-0 rounded-row bg-primary-600 px-3 py-1.5 text-sm font-semibold text-white hover:bg-primary-0"
						>
							Save
						</button>
					</form>
				{:else}
					<h1 class="font-display text-xl font-extralight tracking-tight sm:text-3xl">
						{playlist.name}
					</h1>
				{/if}

				<p class="font-mono text-[0.68rem] uppercase tracking-[0.13em] text-fog">
					{playlist.trackCount}
					{playlist.trackCount === 1 ? 'track' : 'tracks'} · {length} · {playlist.isPublic
						? 'public'
						: 'private'}
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

					{#if mine}
						<button
							type="button"
							class="min-h-9 rounded-row border border-haze px-3 py-1.5 text-sm font-semibold hover:bg-surface-200"
							onclick={() => {
								draftName = playlist?.name ?? '';
								renaming = !renaming;
							}}
						>
							{renaming ? 'Cancel' : 'Edit'}
						</button>
						<button
							type="button"
							class="min-h-9 rounded-row border border-haze px-3 py-1.5 text-sm font-semibold hover:bg-surface-200"
							onclick={toggleVisibility}
						>
							{playlist.isPublic ? 'Make private' : 'Make public'}
						</button>

						<!-- a file input styled as a button: the native picker is the whole feature -->
						<label
							class="flex min-h-9 cursor-pointer items-center rounded-row border border-haze px-3 py-1.5 text-sm font-semibold hover:bg-surface-200 focus-within:ring-2 focus-within:ring-primary-500"
						>
							{playlist.coverUrl ? 'Change cover' : 'Add cover'}
							<input
								type="file"
								accept="image/png,image/jpeg,image/webp"
								class="sr-only"
								onchange={chooseCover}
							/>
						</label>

						{#if confirmingDelete}
							<span class="flex items-center gap-2 text-sm">
								<span class="text-fog">Delete {playlist.name}?</span>
								<button
									type="button"
									class="min-h-9 rounded-row border border-ember px-3 py-1.5 text-sm font-semibold text-ember hover:bg-surface-200"
									onclick={remove_}
								>
									Delete
								</button>
								<button
									type="button"
									class="min-h-9 rounded-row border border-haze px-3 py-1.5 text-sm font-semibold hover:bg-surface-200"
									onclick={() => (confirmingDelete = false)}
								>
									Keep
								</button>
							</span>
						{:else}
							<button
								type="button"
								class="min-h-9 rounded-row border border-haze px-3 py-1.5 text-sm font-semibold text-fog hover:bg-surface-200 hover:text-chalk"
								onclick={() => (confirmingDelete = true)}
							>
								Delete playlist
							</button>
						{/if}
					{/if}
				</div>
			</div>
		</header>

		{#if playlists.error}
			<p class="px-4 text-sm text-ember sm:px-8">{playlists.error}</p>
		{/if}

		<section class="px-2 sm:px-8">
			{#if tracks.length === 0}
				<p class="max-w-lg p-2 text-fog">
					Nothing in this playlist yet. Add tracks from search or the library.
				</p>
			{:else}
				<div class="flex flex-col" {@attach mine && reorder(move)}>
					{#each tracks as track, index (track.id + index)}
						<div
							data-index={index}
							role="button"
							tabindex="0"
							class="group flex cursor-pointer items-center gap-3 rounded-row px-2 py-2 hover:bg-surface-100 active:bg-surface-200 focus-visible:bg-surface-100 focus-visible:outline-none"
							class:select-none={mine}
							onclick={(event) => playUnlessLink(track, event)}
							onkeydown={pressKeys(() => queue.playNow(track))}
						>
							<!-- your own list: the number and the sleeve pick a row up, as in the queue -->
							<span
								data-grip
								class="flex shrink-0 items-center gap-3 self-stretch"
								class:touch-none={mine}
								class:cursor-grab={mine}
							>
								<span class="w-6 text-right font-mono text-[0.68rem] text-fog">{index + 1}</span>
								<img
									src={track.thumbnailUrl ?? '/empty.png'}
									alt=""
									draggable="false"
									class="size-10 rounded-art object-cover"
								/>
							</span>
							<div class="min-w-0 flex-1">
								<p class="truncate text-sm">{track.name}</p>
								<p class="truncate text-xs text-fog"><ArtistLink artist={track.artist} /></p>
							</div>
							<span class="shrink-0 font-mono text-[0.68rem] text-fog">
								{getTimeString(convertTimeSpanStringToSeconds(track.duration))}
							</span>
							{#if mine}
								<button
									type="button"
									aria-label={`Remove ${track.name} from ${playlist.name}`}
									class="flex size-9 shrink-0 items-center justify-center rounded-art text-fog hover:bg-surface-200 hover:text-chalk focus-visible:opacity-100 group-hover:opacity-100 group-focus-visible:opacity-100 pointer-fine:opacity-0"
									onclick={(event) => {
										event.stopPropagation();
										remove(index);
									}}
								>
									×
								</button>
							{/if}
						</div>
					{/each}
				</div>
			{/if}
		</section>
	{/if}
</div>
