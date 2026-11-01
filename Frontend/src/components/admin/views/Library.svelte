<script lang="ts">
	import { untrack } from 'svelte';
	import { SvelteSet } from 'svelte/reactivity';
	import Action from '../Action.svelte';
	import Ask from '../Ask.svelte';
	import Cards from '../Cards.svelte';
	import { MIXED, batchFields, clock, shared, type BatchValues } from '$lib/admin';
	import { read, type Snapshot } from '$requests/admin';
	import fleet from '$states/fleet.svelte';

	type Song = { id: string; titles: string[]; artists: string[]; album: string | null; duration: string; location: string | null };
	type Local = { songs: number; folders: number; withoutAlbum: number; withoutArtist: number; storage: string; scanning: boolean };

	/**
	 * gaida-local: the library's numbers, a rescan, and the library itself — a search, the first
	 * 200 hits, a single-song editor, and a batch editor for a selection. Edits go one request per
	 * song through `edit-song`, which already means "change what I sent, leave the rest", and one
	 * row per song is what the audit log should show.
	 */
	let { name, snapshot }: { name: string; snapshot: Snapshot } = $props();
	let s = $derived(snapshot as unknown as Local);
	let allowed = $derived(fleet.may(name));

	let query = $state('');
	let rows = $state<Song[] | null>(null);
	let loadError = $state('');
	const picked = new SvelteSet<string>();
	let editing = $state<string | null>(null);
	let single = $state<BatchValues>({ titles: '', artists: '', album: '' });
	/** What the batch panel opened with — "unmodified" is measured against it — and what is in its boxes. */
	let batch = $state<BatchValues | null>(null);
	let draft = $state<BatchValues>({ titles: '', artists: '', album: '' });
	let progress = $state('');
	let note = $state<{ text: string; failed: boolean } | null>(null);
	let confirming = $state(false);
	let summary = $state<string[]>([]);

	const lines = (values: string[]) => values.join('\n');
	const message = (error: unknown) => (error instanceof Error ? error.message : 'Failed.');

	async function load(event?: SubmitEvent) {
		event?.preventDefault();
		loadError = '';
		try {
			rows = await read<Song[]>(name, 'library', { take: 200, q: query });
			// a new search is a new set of rows, and a selection you cannot see is one you cannot check
			picked.clear();
			batch = null;
			editing = null;
		} catch (error) {
			loadError = message(error);
		}
	}

	// once, when opened; after that only Search loads, so typing never refetches
	$effect(() => {
		untrack(() => load());
	});

	let chosen = $derived((rows ?? []).filter((row) => picked.has(row.id)));
	let all = $derived(!!rows?.length && picked.size === rows.length);

	function pick(id: string, on: boolean) {
		if (on) picked.add(id);
		else picked.delete(id);
		if (!picked.size) batch = null;
	}

	function edit(song: Song) {
		editing = song.id;
		batch = null;
		single = { titles: lines(song.titles), artists: lines(song.artists), album: song.album ?? '' };
	}

	function replace(saved: Song) {
		rows = (rows ?? []).map((row) => (row.id === saved.id ? saved : row));
	}

	const params = (fields: URLSearchParams, id: string) => new URLSearchParams([['id', id], ...fields]);
	const each = (name: string, text: string) =>
		text
			.split('\n')
			.map((line) => line.trim())
			.filter(Boolean)
			.map((line) => [name, line]);

	async function saveSingle(song: Song) {
		const fields = new URLSearchParams([
			...each('title', single.titles),
			...each('artist', single.artists),
			['album', single.album.trim()]
		]);
		note = null;
		try {
			replace(await fleet.act<Song>(name, 'edit-song', params(fields, song.id)));
			editing = null;
		} catch (error) {
			note = { text: `Saving failed: ${message(error)}`, failed: true };
		}
	}

	function openBatch() {
		batch = {
			titles: shared(chosen, (song) => lines(song.titles)),
			artists: shared(chosen, (song) => lines(song.artists)),
			album: shared(chosen, (song) => song.album ?? '')
		};
		draft = { ...batch };
		editing = null;
		note = null;
	}

	function askBatch() {
		const result = batchFields(batch!, draft);
		if (result.error) return void (note = { text: result.error, failed: true });
		if (!result.summary.length) return void (note = { text: 'Nothing was changed, so nothing was written.', failed: false });
		summary = result.summary;
		confirming = true;
	}

	async function saveBatch() {
		const { fields } = batchFields(batch!, draft);
		const songs = chosen;
		const failures: string[] = [];
		for (const [index, song] of songs.entries()) {
			progress = `Applying ${index + 1} of ${songs.length}…`;
			try {
				replace(await fleet.act<Song>(name, 'edit-song', params(fields, song.id)));
			} catch (error) {
				failures.push(`${song.titles[0] ?? song.id}: ${message(error)}`);
			}
		}
		progress = '';
		batch = null;
		note = failures.length
			? { text: `${songs.length - failures.length} of ${songs.length} saved.\n${failures.slice(0, 10).join('\n')}`, failed: true }
			: { text: `${songs.length} saved.`, failed: false };
	}

	// for artwork replaced on disk, which no scan looks at twice; the table shows no covers, so
	// success is reported too
	async function refreshCovers() {
		const songs = chosen;
		const failures: string[] = [];
		for (const [index, song] of songs.entries()) {
			progress = `Reading ${index + 1} of ${songs.length}…`;
			try {
				await fleet.act(name, 'refresh-cover', { id: song.id });
			} catch (error) {
				failures.push(`${song.titles[0] ?? song.id}: ${message(error)}`);
			}
		}
		progress = '';
		note = {
			text: `${songs.length - failures.length} of ${songs.length} cover(s) re-read.${failures.length ? `\n${failures.slice(0, 10).join('\n')}` : ''}`,
			failed: failures.length > 0
		};
	}

	const label = 'eyebrow mb-1 block';
	const box = 'admin-field w-full min-w-0 px-2 py-1.5 font-mono text-xs sm:w-64';
	const title = $derived(allowed ? undefined : fleet.needs(name));
</script>

<Cards
	items={[
		{ value: s.songs, label: 'songs' },
		{ value: s.folders, label: 'folders' },
		{ value: s.withoutAlbum, label: 'no album' },
		{ value: s.withoutArtist, label: 'no artist' }
	]}
/>

<p class="mb-3 text-sm text-fog">Storage: <span class="font-mono text-xs text-chalk">{s.storage}</span></p>

<div class="mb-6 flex flex-wrap items-center gap-2">
	{#if s.scanning}
		<span class="text-xs text-gold">Scanning the library…</span>
	{:else}
		<Action target={name} action="rescan" label="Rescan library" />
	{/if}
	<span class="text-xs text-fog">Picks up files added to storage since the pod started.</span>
</div>

<h3 class="eyebrow mb-2">Library</h3>

<div class="mb-2.5 flex flex-wrap items-center gap-2">
	<form class="flex min-w-0 flex-1 basis-64 gap-2" onsubmit={load}>
		<input
			type="search"
			bind:value={query}
			placeholder="Search titles, artists, albums or paths…"
			aria-label="Search the library"
			class="admin-field w-full max-w-md min-w-0 px-2.5"
		/>
		<button type="submit" class="admin-button">Search</button>
	</form>
	{#if rows?.length}
		<button
			type="button"
			class="admin-button"
			onclick={() => {
				const select = !all;
				picked.clear();
				if (select) for (const row of rows!) picked.add(row.id);
				else batch = null;
			}}>{all ? 'Clear selection' : `Select all ${rows.length}`}</button
		>
	{/if}
	{#if picked.size && !batch}
		<button type="button" class="admin-button" disabled={!allowed || !!progress} {title} onclick={openBatch}
			>Edit {picked.size} selected</button
		>
		<button type="button" class="admin-button" disabled={!allowed || !!progress} {title} onclick={refreshCovers}
			>Re-read {picked.size} cover{picked.size === 1 ? '' : 's'}</button
		>
	{/if}
	<span class="text-xs text-fog">{picked.size ? `${picked.size} of ` : ''}{rows?.length ?? 0} row(s), 200 max</span>
	{#if progress}<span class="text-xs text-gold" aria-live="polite">{progress}</span>{/if}
</div>

{#if note}
	<p class="mb-2.5 text-sm whitespace-pre-line {note.failed ? 'text-ember' : 'text-fog'}" aria-live="polite">{note.text}</p>
{/if}

{#if batch}
	<div class="mb-2.5 rounded-panel border border-haze bg-surface-0 p-3.5">
		<p class="mb-2 text-[0.8125rem]">
			<b class="font-semibold text-chalk">Editing {picked.size} song{picked.size === 1 ? '' : 's'}</b>
			<span class="text-fog">A field left as it is stays as it is on every song. {MIXED} means they don't agree yet.</span>
		</p>
		<div class="flex flex-wrap gap-3.5">
			<div>
				<label class={label} for="bt-titles">Title variants</label>
				<textarea id="bt-titles" rows="3" class="{box} {batch.titles === MIXED ? 'border-dashed text-fog' : ''}" bind:value={draft.titles}
				></textarea>
				<p class="text-xs text-fog">One per line. The first is the display name.</p>
			</div>
			<div>
				<label class={label} for="bt-artists">Artist variants</label>
				<textarea id="bt-artists" rows="3" class="{box} {batch.artists === MIXED ? 'border-dashed text-fog' : ''}" bind:value={draft.artists}
				></textarea>
				<p class="text-xs text-fog">One per line. Compound names like "A &amp; B" stay on one line.</p>
			</div>
			<div>
				<label class={label} for="bt-album">Album</label>
				<input id="bt-album" class="{box} {batch.album === MIXED ? 'border-dashed text-fog' : ''}" bind:value={draft.album} />
				<p class="text-xs text-fog">Empty clears it on every selected song.</p>
			</div>
		</div>
		<div class="mt-2.5">
			<button type="button" class="admin-button" disabled={!!progress} onclick={askBatch}>Apply to {picked.size}</button>
			<button type="button" class="admin-button" onclick={() => (batch = null)}>Cancel</button>
		</div>
	</div>
{/if}

<div class="admin-scroll">
	{#if loadError}
		<p class="admin-empty text-ember!">{loadError}</p>
	{:else if !rows}
		<p class="admin-empty">Loading…</p>
	{:else if !rows.length}
		<p class="admin-empty">Nothing matches.</p>
	{:else}
		<table class="admin-table">
			<thead><tr><th class="w-px"></th><th>Artist</th><th>Title</th><th>Album</th><th>Length</th><th></th></tr></thead>
			<tbody>
				{#each rows as song (song.id)}
					{@const on = picked.has(song.id)}
					<tr class={on ? 'shadow-[inset_2px_0_0_var(--color-primary-0)]' : ''}>
						<td>
							<input
								type="checkbox"
								checked={on}
								onchange={(event) => pick(song.id, event.currentTarget.checked)}
								aria-label="Select {song.titles[0] ?? song.id}"
								class="rounded-[3px] border-haze bg-dark-0 text-primary-600"
							/>
						</td>
						<td class="wrap">{song.artists[0] ?? '—'}</td>
						<td class="wrap">{song.titles[0] ?? '—'}</td>
						<td class="wrap">{song.album ?? '—'}</td>
						<td class="mono">{clock(song.duration)}</td>
						<td><button type="button" class="admin-button" onclick={() => edit(song)}>Edit</button></td>
					</tr>
					{#if editing === song.id}
						<tr>
							<td colspan="6" class="bg-dark-0">
								<div class="flex flex-wrap gap-3.5 py-1">
									<div>
										<label class={label} for="ed-titles">Title variants</label>
										<textarea id="ed-titles" rows="3" class={box} bind:value={single.titles}></textarea>
										<p class="text-xs text-fog">One per line. The first is the display name.</p>
									</div>
									<div>
										<label class={label} for="ed-artists">Artist variants</label>
										<textarea id="ed-artists" rows="3" class={box} bind:value={single.artists}></textarea>
										<p class="text-xs text-fog">One per line. Compound names like "A &amp; B" stay on one line.</p>
									</div>
									<div>
										<label class={label} for="ed-album">Album</label>
										<input id="ed-album" class={box} bind:value={single.album} />
										<p class="text-xs text-fog">Empty clears it.</p>
									</div>
								</div>
								<div class="mt-2 whitespace-normal">
									<button type="button" class="admin-button" disabled={!allowed} {title} onclick={() => saveSingle(song)}>Save</button>
									<button type="button" class="admin-button" onclick={() => (editing = null)}>Cancel</button>
									<span class="font-mono text-xs break-all text-fog">{song.id} · {song.location ?? ''}</span>
								</div>
							</td>
						</tr>
					{/if}
				{/each}
			</tbody>
		</table>
	{/if}
</div>

<Ask
	bind:open={confirming}
	message={`Apply to ${picked.size} song(s)?\n\n${summary.join('\n')}\n\nThis cannot be undone.`}
	confirmLabel="Apply"
	danger
	onanswer={saveBatch}
/>
