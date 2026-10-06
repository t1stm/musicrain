<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { resolve } from '$app/paths';
	import ArtistLink from '$components/ArtistLink.svelte';
	import PlaylistCover from '$components/playlist/PlaylistCover.svelte';
	import Switch from '$components/settings/Switch.svelte';
	import SwipeRow from '$components/SwipeRow.svelte';
	import TrackMenu from '$components/TrackMenu.svelte';
	import { EllipsisHorizontal, Plus, QueueList } from 'svelte-hero-icons';
	import { convertTimeSpanStringToSeconds, getTimeString, pressKeys } from '$lib';
	import {
		getPlaylist,
		type Playlist,
		type PlaylistTrack,
		type Visibility
	} from '$requests/playlists';
	import { closeOnBack } from '$lib/backWatcher.svelte';
	import { keysOf, nearEnd, STEP } from '$lib/paging';
	import { reorder } from '$lib/reorder';
	import account from '$states/account.svelte';
	import friends from '$states/friends.svelte';
	import playlists, { toSnapshot } from '$states/playlists.svelte';
	import queue from '$states/queue.svelte';
	import type { SearchResult } from '$states/search.svelte';

	// No `+page.ts`: the bearer token lives in `localStorage`, so the fetch belongs
	// where the account state already is — the same reason `/room` reads its own id.
	let id = $derived(page.url.searchParams.get('id') ?? '');
	let playlist = $state<Playlist | null>(null);
	let missing = $state(false);
	let renaming = $state(false);
	let confirmingDelete = $state(false);
	let sharing = $state(false);
	let draftName = $state('');
	/** A save found the tracks changed by somebody else; the page shows theirs now. */
	let stale = $state(false);

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
	closeOnBack(
		() => sharing,
		() => (sharing = false)
	);

	// the row a replacement just landed in, for as long as its ripple runs
	let landedAt = $state<number | null>(null);
	let settle: ReturnType<typeof setTimeout>;

	const same = (a: string | null | undefined, b: string | null | undefined) =>
		!!a && !!b && a.toLowerCase() === b.toLowerCase();

	let tracks = $derived(playlist?.tracks ?? []);
	let keys = $derived(keysOf(tracks));
	/** Drawn from the top, more as the reader nears the end; everything else reads `tracks`. */
	let shown = $state(STEP);
	/** The owner: everything on the page is theirs to change. */
	let mine = $derived(!!playlist && playlist.owner === account.username);
	/** The owner or a friend they share it with: the tracks, and nothing else. */
	let canEdit = $derived(
		mine || (!!playlist && playlist.collaborators.some((name) => same(name, account.username)))
	);
	/** Who added what is only worth saying once somebody besides the owner can add. */
	let shared = $derived((playlist?.collaborators.length ?? 0) > 0);
	let length = $derived(getTimeString(convertTimeSpanStringToSeconds(playlist?.duration ?? '00:00:00')));

	/** What the chosen visibility means, said under the picker. */
	const visibilityHelp: Record<Visibility, string> = {
		private: 'Only you, and the friends who can edit it.',
		friends: 'Your friends can open it. Nobody else can.',
		public: 'Anyone with the link can open it, and it shows under Playlists.'
	};

	/** Whoever is looking reads their own additions as "you"; `null` is the owner's. */
	function addedBy(track: PlaylistTrack) {
		const who = track.addedBy ?? playlist?.owner ?? '';
		return same(who, account.username) ? 'you' : who;
	}

	/** Resolves when the read lands; `live` lets an effect that has moved on ignore it. */
	async function load(token: string | null, live = () => true) {
		try {
			const found = await getPlaylist(id, token);
			if (!live()) return;
			// another playlist starts at the top; a reload of this one keeps the reader's place
			if (found.id !== playlist?.id) shown = STEP;
			playlist = found;
			missing = false;
		} catch {
			if (live()) missing = true;
		}
	}

	$effect(() => {
		// re-runs when a token arrives, which is what turns a 404 on a private
		// playlist into the page its owner is expecting
		const token = account.token;
		if (!id) return;

		let live = true;
		load(token, () => live);

		return () => {
			live = false;
		};
	});

	// the friends the Share panel lists, read when it first opens
	$effect(() => {
		if (sharing && !friends.loaded) friends.load();
	});

	/**
	 * Saves go one at a time, each from the revision the one before it left, so two quick
	 * edits never race each other into a conflict. `pending` counts the edits not yet saved:
	 * while there are some, an answer takes only the revision, not the list, or it would put
	 * back a row the next edit has already moved.
	 */
	let saving: Promise<void> = Promise.resolve();
	let pending = 0;

	/** Every edit is the same shape: change the list here, then send the list. */
	function commit(next: PlaylistTrack[]) {
		if (!playlist) return;
		const id = playlist.id;
		playlist = { ...playlist, tracks: next, trackCount: next.length };
		stale = false;
		pending++;

		saving = saving.then(async () => {
			pending--;
			// somebody else's list replaced this edit, or this is another playlist now
			if (stale || playlist?.id !== id) return;

			const saved = await playlists.update(id, { tracks: playlist.tracks, revision: playlist.revision });

			if (saved === 'stale') {
				// somebody else changed it first: show theirs, and drop what is still queued
				stale = true;
				await load(account.token);
			} else if (saved && playlist) {
				playlist = pending === 0 ? saved : { ...playlist, revision: saved.revision };
			}
		});
	}

	function remove(index: number) {
		commit(tracks.filter((_, at) => at !== index));
	}

	function replaceAt(index: number, result: SearchResult) {
		landedAt = index;
		clearTimeout(settle);
		settle = setTimeout(() => (landedAt = null), 700);
		commit(tracks.with(index, toSnapshot(result)));
	}

	function move(from: number, to: number) {
		const next = [...tracks];
		const [moved] = next.splice(from, 1);
		next.splice(to, 0, moved);
		commit(next);
	}

	// see ArtistLink: the artist name is a real link, so the row must not act on it —
	// and a press anywhere in the menu is the menu's
	function playUnlessLink(track: SearchResult, event: MouseEvent) {
		if (!(event.target as HTMLElement).closest('a, details')) queue.playNow(track);
	}

	// The rows are an #each, not components, so the one open menu is held here by position.
	let menuAt = $state<number | null>(null);
	function setMenu(index: number, open: boolean) {
		if (open) menuAt = index;
		else if (menuAt === index) menuAt = null;
	}

	async function rename(event: SubmitEvent) {
		event.preventDefault();
		if (!playlist || !draftName.trim()) return;

		const saved = await playlists.update(playlist.id, { name: draftName.trim() });
		if (saved && saved !== 'stale' && playlist) playlist = { ...playlist, name: saved.name };
		renaming = false;
	}

	async function setVisibility(visibility: Visibility) {
		if (!playlist) return;

		const was = playlist.visibility;
		playlist = { ...playlist, visibility };
		const saved = await playlists.update(playlist.id, { visibility });
		if (!playlist) return;
		if (saved && saved !== 'stale') playlist = { ...playlist, visibility: saved.visibility };
		// refused or unreachable: the picker goes back, unless it has been moved again since
		else if (playlist.visibility === visibility) playlist = { ...playlist, visibility: was };
	}

	const toggled = (list: string[], name: string, on: boolean) =>
		on ? [...list, name] : list.filter((editor) => !same(editor, name));

	function isEditor(name: string) {
		return !!playlist?.collaborators.some((editor) => same(editor, name));
	}

	/** The whole list goes each time: Dom replaces it, and checks every name is a friend. */
	async function setEditor(name: string, on: boolean) {
		if (!playlist) return;

		const collaborators = toggled(playlist.collaborators, name, on);
		playlist = { ...playlist, collaborators };

		const saved = await playlists.update(playlist.id, { collaborators });
		if (!playlist) return;
		if (saved && saved !== 'stale') playlist = { ...playlist, collaborators: saved.collaborators };
		// refused or unreachable: this switch goes back, and only this one
		else if (isEditor(name) === on)
			playlist = { ...playlist, collaborators: toggled(playlist.collaborators, name, !on) };
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
				<p class="eyebrow">
					{mine ? 'Your playlist' : `${playlist.owner}’s playlist`}{#if canEdit && !mine}&nbsp;·
						<span class="text-primary-500">you can edit</span>{/if}
				</p>

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
					{playlist.trackCount === 1 ? 'track' : 'tracks'} · {length} · {playlist.visibility}{#if shared}&nbsp;·
						{playlist.collaborators.length} can edit{/if}
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
							aria-expanded={sharing}
							aria-controls="share"
							class="min-h-9 rounded-row border border-haze px-3 py-1.5 text-sm font-semibold hover:bg-surface-200"
							class:border-primary-0={sharing}
							class:bg-surface-200={sharing}
							onclick={() => (sharing = !sharing)}
						>
							Share
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

		{#if mine && sharing}
			<!-- Both of the playlist's sharing controls in one place: who can open it, and
			     which friends can change its tracks. -->
			<section
				id="share"
				aria-label={`Share ${playlist.name}`}
				class="mx-3 flex flex-col gap-2.5 rounded-panel border border-haze bg-surface-100 p-4 sm:mx-8 sm:max-w-md"
			>
				<label for="visibility" class="eyebrow flex items-center gap-3">
					Who can see it
					<span class="h-px flex-1 bg-haze"></span>
				</label>
				<!-- native: a phone gets its own picker -->
				<select
					id="visibility"
					value={playlist.visibility}
					onchange={(event) => setVisibility(event.currentTarget.value as Visibility)}
					class="min-h-11 w-full rounded-row border border-haze bg-dark-0 text-chalk ring-primary-0 focus:border-primary-0 focus-visible:ring-2"
				>
					<option value="private">Private</option>
					<option value="friends">Friends</option>
					<option value="public">Public</option>
				</select>
				<p class="text-sm text-fog">{visibilityHelp[playlist.visibility]}</p>

				<h2 class="eyebrow mt-2.5 flex items-center gap-3">
					Who can edit
					<span class="h-px flex-1 bg-haze"></span>
				</h2>
				{#if friends.loaded && friends.list.length === 0}
					<p class="text-sm text-fog">
						Add friends first, then share this playlist with them. <a
							href={resolve('/settings/friends')}
							class="text-primary-500 underline-offset-4 hover:underline">Add friends</a
						>
					</p>
				{:else}
					<div class="flex flex-col">
						{#each friends.list as name (name)}
							<label class="flex min-h-12 cursor-pointer items-center gap-3 border-b border-haze">
								<span
									aria-hidden="true"
									class="grid size-8 shrink-0 place-items-center rounded-full bg-surface-200 text-[0.8rem] font-semibold text-primary-500"
									>{name[0]}</span
								>
								<span class="min-w-0 flex-1 truncate">{name}</span>
								<Switch bind:checked={() => isEditor(name), (on) => setEditor(name, on)} />
							</label>
						{/each}
					</div>
					<p class="text-sm text-fog">
						They can add, remove and reorder tracks. Only you can rename, share or delete this
						playlist.
					</p>
				{/if}
			</section>
		{/if}

		{#if stale}
			<p role="status" class="px-4 text-sm text-primary-500 sm:px-8">
				Someone else changed this playlist while you were editing. This is the latest.
			</p>
		{/if}

		{#if playlists.error}
			<p class="px-4 text-sm text-ember sm:px-8">{playlists.error}</p>
		{/if}

		<section class="px-2 sm:px-8">
			{#if tracks.length === 0}
				<p class="max-w-lg p-2 text-fog">
					Nothing in this playlist yet. Add tracks from search or the library.
				</p>
			{:else}
				<div class="flex flex-col" {@attach canEdit && reorder(move)}>
					<!-- sliced from the top, so `index` is still the track's place in the playlist -->
					{#each tracks.slice(0, shown) as track, index (keys[index])}
						<!-- a list you can edit: the grip is the reorder's, so a swipe starts anywhere else -->
						<SwipeRow
							ignore={canEdit ? '[data-grip]' : undefined}
							right={[
								{
									icon: Plus,
									color: 'var(--color-surface-400)',
									done: 'Queued',
									run: () => queue.add(track)
								},
								{
									icon: QueueList,
									color: 'var(--color-primary-600)',
									done: 'Next Up',
									run: () => queue.playNext(track)
								}
							]}
							left={{
								icon: EllipsisHorizontal,
								label: 'More',
								color: 'var(--color-surface-300)',
								run: () => setMenu(index, true)
							}}
						>
							<div
								data-index={index}
								data-preview
								role="button"
								tabindex="0"
								class="group flex cursor-pointer items-center gap-3 rounded-row px-2 py-2 not-has-open:hover:bg-surface-100 not-has-open:active:bg-surface-200 focus-visible:bg-surface-100 focus-visible:outline-none"
								class:select-none={canEdit}
								onclick={(event) => playUnlessLink(track, event)}
								onkeydown={pressKeys(() => queue.playNow(track))}
							>
								<!-- a list you can edit: the number and the sleeve pick a row up, as in the queue -->
								<span
									data-grip
									class="flex shrink-0 items-center gap-3 self-stretch"
									class:touch-none={canEdit}
									class:cursor-grab={canEdit}
								>
									<span class="w-6 text-right font-mono text-[0.68rem] text-fog">{index + 1}</span>
									<img
										src={track.thumbnailUrl ?? '/empty.png'}
										alt=""
										draggable="false"
										class="size-10 rounded-art object-cover motion-reduce:animate-none"
										class:animate-ripple={landedAt === index}
									/>
								</span>
								<div class="min-w-0 flex-1">
									<p class="truncate text-sm">{track.name}</p>
									<p class="truncate text-xs text-fog">
										<ArtistLink artist={track.artist} />{#if shared}&nbsp;·
											<!-- a friend's name in the app's violet; your own additions say "you" -->
											<span class:text-primary-500={addedBy(track) !== 'you'}>{addedBy(track)}</span
											>{/if}
									</p>
								</div>
								<span class="shrink-0 font-mono text-[0.68rem] text-fog">
									{getTimeString(convertTimeSpanStringToSeconds(track.duration))}
								</span>
								<!-- a phone reaches it with a swipe; the row has no width left for a third button -->
								<TrackMenu
									result={track}
									bind:open={() => menuAt === index, (open) => setMenu(index, open)}
									replace={canEdit ? (result) => replaceAt(index, result) : undefined}
									class="max-sm:[&>summary]:hidden pointer-fine:opacity-0 pointer-fine:group-hover:opacity-100 pointer-fine:group-focus-within:opacity-100 pointer-fine:open:opacity-100"
								/>
								{#if canEdit}
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
						</SwipeRow>
					{/each}
				</div>
				<!-- Outside the list, so the reorder never takes it for a row. A drag drops among
				     the rows already drawn: the ones it scrolls in are drawn after it began. -->
				{#if shown < tracks.length}
					<button
						type="button"
						class="mx-2 mt-2 min-h-11 rounded-row border border-haze px-3 py-2 font-mono text-[0.68rem] uppercase tracking-[0.13em] text-fog hover:bg-surface-200 hover:text-chalk"
						onclick={() => (shown += STEP)}
						{@attach nearEnd(() => (shown += STEP))}
					>
						Show more · {tracks.length - shown} left
					</button>
				{/if}
			{/if}
		</section>
	{/if}
</div>
