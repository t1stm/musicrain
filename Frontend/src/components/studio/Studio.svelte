<script lang="ts">
	/*
	 * The lyrics studio, for this machine only: the route is dev-only and the back end is Tools/Lirika's
	 * studio.py on 127.0.0.1. Pick a library song, start from textove's words (or your last edit),
	 * edit them, regenerate in map mode, then fix lines by hand. Every keystroke in the LRC goes to the
	 * real lyrics pane through lyrics.showDraft, so the full player shows exactly what a listener would.
	 */
	import { onDestroy, onMount } from 'svelte';
	import audio from '$states/audio.svelte';
	import current from '$states/current.svelte';
	import lyrics from '$states/lyrics.svelte';
	import queue from '$states/queue.svelte';
	import type { SearchResult } from '$states/search.svelte';
	import { getSearch } from '$requests/search';
	import type { Lyrics, LyricLine } from '$requests/lyrics';
	import { seekTo } from '$lib/seek';

	const LIRIKA = 'http://127.0.0.1:8765';

	type Candidate = { source: string; label: string; text: string; url?: string };
	type Flagged = { start: number; end: number; text: string; flags: string[] };

	let server = $state<'checking' | 'up' | 'down'>('checking');
	let query = $state('');
	let results = $state<SearchResult[]>([]);
	let candidates = $state<Candidate[]>([]);
	let chosen = $state(0);
	let source = $state('');
	let lrc = $state('');
	let flagged = $state<Flagged[]>([]);
	let busy = $state('');
	let note = $state('');
	let link = $state('');
	let loadedFor = '';
	// The LRC as Lirika wrote it or as last saved: a save that differs from it marks the file edited.
	let untouched = '';
	let editor = $state<HTMLTextAreaElement | null>(null);

	let track = $derived(current.id.startsWith('audio://') ? current.id : '');

	onMount(async () => {
		try {
			server = (await fetch(`${LIRIKA}/health`)).ok ? 'up' : 'down';
		} catch {
			server = 'down';
		}
	});

	// The playing track is the one being worked on; a new one loads its texts once.
	$effect(() => {
		if (server === 'up' && track && track !== loadedFor) void loadTexts(track);
	});

	// Every edit of the LRC is the draft the pane shows, a beat after typing stops.
	$effect(() => {
		const text = lrc;
		const id = track;
		if (!id) return;
		const timer = setTimeout(() => {
			lyrics.showDraft(id, text.trim() ? draftOf(text) : null);
			if (text.trim()) lyrics.open = true;
		}, 150);
		return () => clearTimeout(timer);
	});

	onDestroy(() => {
		if (track) lyrics.showDraft(track, null);
	});

	async function call<T>(path: string, body?: unknown): Promise<T> {
		const response = await fetch(`${LIRIKA}${path}`, body === undefined ? undefined : {
			method: 'POST',
			headers: { 'Content-Type': 'application/json' },
			body: JSON.stringify(body)
		});
		const payload = await response.json();
		if (!response.ok) throw new Error(payload.error ?? `lirika answered ${response.status}`);
		return payload as T;
	}

	async function loadTexts(id: string) {
		loadedFor = id;
		busy = 'Looking for the words…';
		note = '';
		try {
			const found = await call<{ candidates: Candidate[]; saved: string | null }>(`/text?id=${encodeURIComponent(id)}`);
			candidates = found.candidates;
			chosen = 0;
			source = found.candidates[0]?.text ?? '';
			lrc = found.saved ?? '';
			untouched = lrc;
			flagged = [];
			if (!found.candidates.length) note = 'No text anywhere: paste the words into the source box.';
		} catch (error) {
			note = (error as Error).message;
		} finally {
			busy = '';
		}
	}

	/** A textove page found by hand: the search misses songs listed under another name. */
	async function fetchLink() {
		if (!link.trim()) return;
		busy = 'Fetching the page…';
		note = '';
		try {
			const found = await call<Candidate>(`/link?url=${encodeURIComponent(link.trim())}`);
			candidates = [...candidates, found];
			chosen = candidates.length - 1;
			source = found.text;
			link = '';
		} catch (error) {
			note = (error as Error).message;
		} finally {
			busy = '';
		}
	}

	async function search() {
		if (!query.trim()) return;
		busy = 'Searching…';
		try {
			results = (await getSearch(query, fetch)).filter((result) => result.id.startsWith('audio://'));
		} catch (error) {
			note = (error as Error).message;
		} finally {
			busy = '';
		}
	}

	async function regenerate() {
		if (!track || !source.trim()) return;
		busy = 'Aligning… (the first run of a song also separates it, about half a minute)';
		note = '';
		try {
			const result = await call<{ lrc: string; lines: Flagged[]; songFlags: string[] }>('/generate', { id: track, text: source });
			lrc = result.lrc;
			untouched = lrc;
			flagged = result.lines.filter((line) => line.flags.length);
			note = result.songFlags.join('; ');
		} catch (error) {
			note = (error as Error).message;
		} finally {
			busy = '';
		}
	}

	async function save() {
		if (!track) return;
		try {
			if (lrc !== untouched) lrc = markedEdited(lrc);
			const saved = await call<{ lrc: string }>('/save', { id: track, lrc, text: source });
			untouched = lrc;
			note = `Saved to ${saved.lrc}`;
		} catch (error) {
			note = (error as Error).message;
		}
	}

	/** The [#:edited:…] tag set to yes. A file from before Lirika wrote the tag gets one. */
	function markedEdited(text: string): string {
		const tag = /^\[#:edited:[^\]]*\]$/m;
		return tag.test(text) ? text.replace(tag, '[#:edited:yes]') : `[#:edited:yes]\n${text}`;
	}

	// ── LRC, read the way stih's LrcParser reads it ─────────────────────────────────────────────

	const STAMP = /\[(?:(\d+):)?(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]/g;

	function seconds(match: RegExpMatchArray): number {
		const [, hours, minutes, secs, fraction = ''] = match;
		const part = fraction ? Number(fraction) / 10 ** fraction.length : 0;
		return Number(hours ?? 0) * 3600 + Number(minutes) * 60 + Number(secs) + part;
	}

	function draftOf(text: string): Lyrics {
		const timed: { at: number; text: string; order: number }[] = [];
		for (const raw of text.split('\n')) {
			const stamps = [...raw.matchAll(STAMP)];
			if (!stamps.length) continue; // the [re:…] header and anything untimed
			const last = stamps[stamps.length - 1];
			const words = raw.slice((last.index ?? 0) + last[0].length).trim();
			for (const stamp of stamps) timed.push({ at: seconds(stamp), text: words, order: timed.length });
		}
		const lines: LyricLine[] = timed
			.sort((a, b) => a.at - b.at || a.order - b.order)
			.map(({ at, text }) => ({ at, text }));
		return {
			type: 'Synchronized',
			source: 'Generated',
			lines,
			text: lines.map((line) => line.text).join('\n').trim(),
			matched: null
		};
	}

	function stamp(at: number): string {
		const hundredths = Math.max(0, Math.round(at * 100));
		const minutes = Math.floor(hundredths / 6000);
		const rest = hundredths % 6000;
		const pad = (value: number) => String(value).padStart(2, '0');
		return `[${pad(minutes)}:${pad(Math.floor(rest / 100))}.${pad(rest % 100)}]`;
	}

	/** Rewrites the stamp of the line the cursor is on; `to` gets the line's current time. */
	function restamp(to: (at: number) => number) {
		if (!editor) return;
		const caret = editor.selectionStart;
		const start = lrc.lastIndexOf('\n', caret - 1) + 1;
		const end = lrc.indexOf('\n', caret) === -1 ? lrc.length : lrc.indexOf('\n', caret);
		const line = lrc.slice(start, end);
		const found = [...line.matchAll(STAMP)][0];
		const at = found ? seconds(found) : audio.positionNow();
		const words = found ? line.slice((found.index ?? 0) + found[0].length) : line;
		lrc = lrc.slice(0, start) + stamp(to(at)) + words + lrc.slice(end);
		const position = start + stamp(0).length;
		requestAnimationFrame(() => editor?.setSelectionRange(position, position));
	}

	const atPlayhead = () => restamp(() => audio.positionNow());
	const nudge = (by: number) => restamp((at) => at + by);

	function keys(event: KeyboardEvent) {
		if (!event.altKey) return;
		if (event.key === 'Enter') atPlayhead();
		else if (event.key === 'ArrowUp') nudge(-0.1);
		else if (event.key === 'ArrowDown') nudge(0.1);
		else return;
		event.preventDefault();
	}
</script>

<div class="page page-column gap-6 px-2 py-6 sm:px-8 sm:pb-28">
	<header>
		<p class="eyebrow text-primary-500">Lyrics studio · this machine only</p>
		<h1 class="mt-2 font-display text-xl font-light leading-tight tracking-tight text-chalk sm:text-3xl">
			{current.name ? `${current.artist} — ${current.name}` : 'Play a library song'}
		</h1>
		<p class="mt-2 text-sm text-fog">
			{#if server === 'down'}
				The Lirika server is not running. Start it with <code>uv run studio.py</code> in <code>Tools/Lirika</code>.
			{:else if !track && current.id}
				Only library tracks (audio://) have a file to align.
			{:else}
				Open the full player to see the draft in the real lyrics pane while you edit.
			{/if}
		</p>
	</header>

	<form class="flex gap-2" onsubmit={(event) => { event.preventDefault(); void search(); }}>
		<input class="studio-field flex-1" placeholder="Find a library song" bind:value={query} />
		<button class="studio-button" type="submit">Search</button>
	</form>
	{#if results.length}
		<ul class="flex flex-col gap-1">
			{#each results as result (result.id)}
				<li>
					<button class="studio-row" onclick={() => { queue.playNow(result); results = []; }}>
						{result.artist} — {result.name}
					</button>
				</li>
			{/each}
		</ul>
	{/if}

	{#if busy}<p class="text-sm text-gold">{busy}</p>{/if}
	{#if note}<p class="text-sm text-fog">{note}</p>{/if}

	{#if track}
		<div class="grid gap-6 lg:grid-cols-2">
			<section class="flex flex-col gap-2">
				<div class="flex items-center gap-2">
					<h2 class="font-display text-sm text-chalk">Source text</h2>
					<select class="studio-field ml-auto max-w-[60%] text-sm" bind:value={chosen} disabled={!candidates.length}
						onchange={() => (source = candidates[chosen]?.text ?? source)}>
						{#each candidates as candidate, index (index)}
							<option value={index}>{candidate.label}</option>
						{:else}
							<option value={0}>No text found: paste one, or a textove link below</option>
						{/each}
					</select>
				</div>
				<form class="flex gap-2" onsubmit={(event) => { event.preventDefault(); void fetchLink(); }}>
					<input class="studio-field flex-1 text-sm" placeholder="https://textove.com/…-tekst" bind:value={link} />
					<button class="studio-button" type="submit" disabled={!!busy || !link.trim()}>Fetch</button>
				</form>
				<textarea class="studio-field min-h-[28rem] font-mono text-sm" bind:value={source}
					spellcheck="false"></textarea>
				<p class="text-xs text-fog">
					One sung line per line. Order and repeats do not matter to map mode; a missing line, a wrong
					word or a stanza typed as one line do.
				</p>
				<button class="studio-button self-start" onclick={regenerate} disabled={!!busy || !source.trim()}>
					Regenerate
				</button>
			</section>

			<section class="flex flex-col gap-2">
				<div class="flex items-center gap-2">
					<h2 class="font-display text-sm text-chalk">LRC</h2>
					<span class="ml-auto text-xs text-fog">Alt+Enter: line to playhead · Alt+↑/↓: ∓0.1 s</span>
				</div>
				<textarea class="studio-field min-h-[28rem] font-mono text-sm" bind:value={lrc} bind:this={editor}
					onkeydown={keys} spellcheck="false"></textarea>
				<div class="flex gap-2">
					<button class="studio-button" onclick={atPlayhead}>Line to playhead</button>
					<button class="studio-button" onclick={() => nudge(-0.1)}>−0.1 s</button>
					<button class="studio-button" onclick={() => nudge(0.1)}>+0.1 s</button>
					<button class="studio-button ml-auto" onclick={save} disabled={!lrc.trim()}>Save</button>
				</div>
				{#if flagged.length}
					<h3 class="mt-2 text-xs text-fog">Flagged by Lirika</h3>
					<ul class="flex flex-col gap-1">
						{#each flagged as line (line.start)}
							<li>
								<button class="studio-row" onclick={() => seekTo(line.start)}>
									<span class="font-mono text-fog">{stamp(line.start)}</span>
									{line.text}
									<span class="text-ember">· {line.flags.join(', ')}</span>
								</button>
							</li>
						{/each}
					</ul>
				{/if}
			</section>
		</div>
	{/if}
</div>

<style>
	.studio-field {
		border-radius: var(--radius-row);
		background: color-mix(in srgb, var(--color-haze) 70%, transparent);
		color: var(--color-chalk);
		padding: 0.5rem 0.75rem;
		border: 1px solid color-mix(in srgb, var(--color-fog) 25%, transparent);
	}
	.studio-button {
		border-radius: var(--radius-row);
		background: var(--color-haze);
		color: var(--color-chalk);
		padding: 0.4rem 0.9rem;
		font-size: 0.875rem;
	}
	.studio-button:disabled {
		opacity: 0.5;
	}
	.studio-row {
		width: 100%;
		text-align: left;
		border-radius: var(--radius-row);
		padding: 0.35rem 0.6rem;
		font-size: 0.875rem;
		color: var(--color-chalk);
	}
	.studio-row:hover {
		background: color-mix(in srgb, var(--color-haze) 70%, transparent);
	}
</style>
