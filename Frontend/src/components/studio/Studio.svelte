<script lang="ts">
	/*
	 * The lyrics studio, for this machine only: the route is dev-only and the back end is Tools/Lirika's
	 * studio.py on 127.0.0.1. Pick a library song, start from textove's words (or your last edit),
	 * edit them, regenerate in map mode, then fix lines by hand. Every keystroke in the LRC goes to the
	 * real lyrics pane through lyrics.showDraft, so the full player shows exactly what a listener would.
	 * The stems Lirika separated can be heard in the song's place, following the player's clock.
	 */
	import { onDestroy, onMount, untrack } from 'svelte';
	import audio from '$states/audio.svelte';
	import current from '$states/current.svelte';
	import lyrics from '$states/lyrics.svelte';
	import queue from '$states/queue.svelte';
	import type { SearchResult } from '$states/search.svelte';
	import { getSearch } from '$requests/search';
	import type { Lyrics, LyricLine } from '$requests/lyrics';
	import { seekTo } from '$lib/seek';

	// studio.py runs beside the dev server: a phone on the LAN reaches it through the address it loaded
	// this page from (LIRIKA_STUDIO_LAN). 127.0.0.1 locally, since "localhost" may try IPv6 first.
	const LIRIKA = `http://${location.hostname === 'localhost' ? '127.0.0.1' : location.hostname}:8765`;

	type Candidate = { source: string; label: string; text: string; url?: string };
	type Flagged = { start: number; end: number; text: string; flags: string[] };
	type Ear = 'song' | 'lead' | 'vocals';

	// What each stem is to map mode (studio.py's STEMS): what to listen for when a line lands wrong.
	const EARS: { kind: Ear; label: string; role: string }[] = [
		{ kind: 'song', label: 'Song', role: 'The song as listeners hear it.' },
		{ kind: 'lead', label: 'Lead vocal', role: 'Marks where the song is sung. Whisper transcribes those stretches.' },
		{ kind: 'vocals', label: 'All voices', role: 'The aligner times every word on this.' }
	];

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
	// Below lg the two boxes take turns; side by side above it.
	let pane = $state<'source' | 'lrc'>('source');

	let stems = $state<string[]>([]);
	let ear = $state<Ear>('song');
	let loading = $state<Ear | null>(null);
	let stemUrl = $state('');
	let stemPlayer = $state<HTMLAudioElement | null>(null);
	// The player's own volume while a stem plays in its place, given back when the song returns.
	let level = 0;

	let track = $derived(current.id.startsWith('audio://') ? current.id : '');
	let draft = $derived(lrc.trim() ? draftOf(lrc) : null);
	// The pane is in the full player, which covers the studio on a phone: the line being sung shows here too.
	let now = $derived(
		draft?.lines.findLast((line): line is { at: number; text: string } => line.at !== null && line.at <= audio.currentSeconds) ?? null
	);
	let role = $derived(
		stems.length ? EARS.find((option) => option.kind === ear)!.role
			: 'The lead vocal and all voices arrive with the first Regenerate, which separates them.'
	);

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
		const lines = draft;
		const id = track;
		if (!id) return;
		const timer = setTimeout(() => {
			lyrics.showDraft(id, lines);
			if (lines) lyrics.open = true;
		}, 150);
		return () => clearTimeout(timer);
	});

	onDestroy(() => {
		if (track) lyrics.showDraft(track, null);
	});

	// A stem belongs to one track: the next one, and leaving the page, go back to the song.
	$effect(() => {
		void track;
		return () => untrack(() => hear('song'));
	});

	// The player stays silent while a stem plays in its place; its volume slider sets the stem's.
	$effect(() => {
		if (ear === 'song') return;
		if (audio.volume > 0) {
			level = audio.volume;
			audio.volume = 0;
		}
		if (stemPlayer) stemPlayer.volume = level;
	});

	// The stem follows the player's clock: its play, its pause, and a seek within a quarter second.
	$effect(() => {
		const player = stemPlayer;
		if (!player) return;
		if (ear === 'song' || audio.paused) {
			player.pause();
			return;
		}
		// positionNow reads and writes the output latency, which a phone re-estimates many times a second.
		// Tracked, every new estimate re-ran this effect, and each run's seek was heard as a stutter.
		const at = () => untrack(() => audio.positionNow());
		player.currentTime = at();
		player.play().catch(() => {});
		// A phone takes a few hundred ms to sound again after a seek; its clock stands still meanwhile and
		// must not read as drift, or each seek starts the next.
		let settled = performance.now() + 1000;
		const timer = setInterval(() => {
			if (player.seeking || player.paused || performance.now() < settled) return;
			if (Math.abs(player.currentTime - at()) > 0.25) {
				player.currentTime = at();
				settled = performance.now() + 1000;
			}
		}, 250);
		return () => clearInterval(timer);
	});

	async function hear(kind: Ear) {
		if (kind === ear) return;
		if (kind === 'song') {
			audio.volume = level;
			ear = 'song';
			URL.revokeObjectURL(stemUrl);
			stemUrl = '';
			return;
		}
		const id = track;
		loading = kind;
		note = '';
		try {
			const response = await fetch(`${LIRIKA}/stem?id=${encodeURIComponent(id)}&kind=${kind}`);
			if (!response.ok) throw new Error((await response.json()).error ?? `lirika answered ${response.status}`);
			const blob = await response.blob();
			if (id !== track) return; // the track changed while it downloaded
			URL.revokeObjectURL(stemUrl);
			stemUrl = URL.createObjectURL(blob); // a blob seeks; studio.py answers no Range requests
			ear = kind;
		} catch (error) {
			note = (error as Error).message;
		} finally {
			loading = null;
		}
	}

	async function loadStems(id: string) {
		try {
			stems = (await call<{ stems: string[] }>(`/stems?id=${encodeURIComponent(id)}`)).stems;
		} catch {
			stems = [];
		}
	}

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
		void loadStems(id);
		try {
			const found = await call<{ candidates: Candidate[]; saved: string | null }>(`/text?id=${encodeURIComponent(id)}`);
			candidates = found.candidates;
			chosen = 0;
			source = found.candidates[0]?.text ?? '';
			lrc = found.saved ?? '';
			untouched = lrc;
			flagged = [];
			pane = lrc ? 'lrc' : 'source';
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
			pane = 'lrc';
			void loadStems(track);
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
	// A timing button leaves the focus, and a phone's keyboard, in the LRC box.
	const keepFocus = (event: PointerEvent) => event.preventDefault();

	function keys(event: KeyboardEvent) {
		if (!event.altKey) return;
		if (event.key === 'Enter') atPlayhead();
		else if (event.key === 'ArrowUp') nudge(-0.1);
		else if (event.key === 'ArrowDown') nudge(0.1);
		else return;
		event.preventDefault();
	}
</script>

<div class="page page-column gap-6 px-4 pt-6 pb-4 sm:px-8 sm:pb-28">
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
		<input class="studio-field min-w-0 flex-1" type="search" enterkeyhint="search" placeholder="Find a library song" bind:value={query} />
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

	{#if busy}<p class="text-sm text-gold" aria-live="polite">{busy}</p>{/if}
	{#if note}<p class="text-sm text-fog" aria-live="polite">{note}</p>{/if}

	{#if track}
		<section class="flex flex-wrap items-center gap-x-3 gap-y-2" aria-label="Stems">
			<span class="eyebrow {ear === 'song' ? '' : 'text-gold'}">Listen to</span>
			<div class="studio-switch studio-ears flex max-sm:w-full" role="group" aria-label="Listen to">
				{#each EARS as option (option.kind)}
					<button type="button" data-kind={option.kind} aria-pressed={ear === option.kind} aria-busy={loading === option.kind}
						disabled={option.kind !== 'song' && (!stems.includes(option.kind) || !!loading)}
						onclick={() => hear(option.kind)}>
						{#if loading === option.kind}<span class="rain-streak" aria-hidden="true"></span>{/if}
						<span class="relative">{option.label}</span>
					</button>
				{/each}
			</div>
			<p class="min-w-0 text-xs text-fog sm:flex-1">{role}</p>
		</section>

		<div class="studio-switch flex lg:hidden" role="group" aria-label="Text to edit">
			<button type="button" aria-pressed={pane === 'source'} onclick={() => (pane = 'source')}>Source text</button>
			<button type="button" aria-pressed={pane === 'lrc'} onclick={() => (pane = 'lrc')}>LRC</button>
		</div>

		<div class="grid gap-6 lg:grid-cols-2">
			<section class="flex-col gap-2 {pane === 'source' ? 'flex' : 'hidden lg:flex'}">
				<div class="flex items-center gap-2">
					<h2 class="font-display text-sm text-chalk max-lg:sr-only">Source text</h2>
					<select class="studio-field min-w-0 flex-1 lg:ml-auto lg:max-w-[60%] lg:flex-none" bind:value={chosen} disabled={!candidates.length}
						aria-label="Starting text" onchange={() => (source = candidates[chosen]?.text ?? source)}>
						{#each candidates as candidate, index (index)}
							<option value={index}>{candidate.label}</option>
						{:else}
							<option value={0}>No text found: paste one, or a textove link below</option>
						{/each}
					</select>
				</div>
				<form class="flex gap-2" onsubmit={(event) => { event.preventDefault(); void fetchLink(); }}>
					<input class="studio-field min-w-0 flex-1" type="url" inputmode="url" placeholder="https://textove.com/…-tekst"
						aria-label="textove page link" bind:value={link} />
					<button class="studio-button" type="submit" disabled={!!busy || !link.trim()}>Fetch</button>
				</form>
				<textarea class="studio-field studio-box font-mono" bind:value={source} aria-label="Source text"
					spellcheck="false"></textarea>
				<p class="text-xs text-fog">
					One sung line per line. Order and repeats do not matter to map mode; a missing line, a wrong
					word or a stanza typed as one line do.
				</p>
				<button class="studio-button self-start" onclick={regenerate} disabled={!!busy || !source.trim()}>
					Regenerate
				</button>
			</section>

			<section class="flex-col gap-2 {pane === 'lrc' ? 'flex' : 'hidden lg:flex'}">
				<div class="flex items-center gap-2">
					<h2 class="font-display text-sm text-chalk max-lg:sr-only">LRC</h2>
					<span class="hidden text-xs text-fog pointer-fine:inline lg:ml-auto">Alt+Enter: line to playhead · Alt+↑/↓: ∓0.1 s</span>
					<button class="studio-button ml-auto lg:ml-0" onclick={save} disabled={!lrc.trim()}>Save</button>
				</div>
				<textarea class="studio-field studio-box font-mono" bind:value={lrc} bind:this={editor} aria-label="LRC"
					onkeydown={keys} spellcheck="false"></textarea>
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

		<section class="studio-console" aria-label="Line at the playhead">
			<button type="button" class="studio-row min-w-0 flex-1 truncate" disabled={!now}
				title="Play from this line" onclick={() => now && seekTo(now.at)}>
				<span class="font-mono text-fog">{now ? stamp(now.at) : '[--:--.--]'}</span>
				{now ? now.text || '—' : 'No line at the playhead yet'}
			</button>
			<div class="gap-2 max-sm:w-full {pane === 'lrc' ? 'flex' : 'hidden lg:flex'}">
				<button class="studio-button" onpointerdown={keepFocus} onclick={() => nudge(-0.1)} disabled={!lrc.trim()}
					aria-label="Line 0.1 seconds earlier">−0.1 s</button>
				<button class="studio-button flex-1" onpointerdown={keepFocus} onclick={atPlayhead} disabled={!lrc.trim()}>
					Line to playhead
				</button>
				<button class="studio-button" onpointerdown={keepFocus} onclick={() => nudge(0.1)} disabled={!lrc.trim()}
					aria-label="Line 0.1 seconds later">+0.1 s</button>
			</div>
		</section>
	{/if}
</div>

<audio bind:this={stemPlayer} src={stemUrl || undefined} preload="auto"></audio>

<style>
	.studio-field {
		border-radius: var(--radius-row);
		background: color-mix(in srgb, var(--color-haze) 70%, transparent);
		color: var(--color-chalk);
		padding: 0.5rem 0.75rem;
		border: 1px solid color-mix(in srgb, var(--color-fog) 25%, transparent);
		/* under 16px, iOS zooms the page in on focus */
		font-size: 1rem;
	}
	.studio-box {
		height: 45dvh;
		min-height: 16rem;
		line-height: 1.6;
	}
	.studio-button {
		border-radius: var(--radius-row);
		background: var(--color-haze);
		color: var(--color-chalk);
		padding: 0.4rem 0.9rem;
		font-size: 0.875rem;
		white-space: nowrap;
	}
	.studio-button:disabled {
		opacity: 0.5;
	}
	.studio-row {
		display: block;
		width: 100%;
		text-align: left;
		border-radius: var(--radius-row);
		padding: 0.35rem 0.6rem;
		font-size: 0.875rem;
		color: var(--color-chalk);
	}
	.studio-row:hover:enabled {
		background: color-mix(in srgb, var(--color-haze) 70%, transparent);
	}

	/* The app's segmented control (the artist page's source tabs), as toggles. */
	/* display comes from a class, so lg:hidden can still take it away */
	.studio-switch {
		border-radius: var(--radius-panel);
		border: 1px solid var(--color-haze);
		background: var(--color-dark-0);
		padding: 0.25rem;
	}
	.studio-switch button {
		position: relative;
		overflow: hidden;
		flex: 1;
		min-height: 2.5rem;
		white-space: nowrap;
		border-radius: var(--radius-row);
		padding-inline: 0.75rem;
		font-size: 0.875rem;
		font-weight: 600;
		color: var(--color-fog);
	}
	.studio-switch button:hover:enabled {
		color: var(--color-chalk);
	}
	.studio-switch button[aria-pressed='true'] {
		background: var(--color-primary-600);
		color: white;
	}
	.studio-switch button:disabled {
		opacity: 0.4;
	}
	/* The song is the resting state. A stem in its place is gold: what plays is not what listeners hear. */
	.studio-ears button[aria-pressed='true'] {
		background: var(--color-surface-300);
		color: var(--color-chalk);
	}
	.studio-ears button[aria-pressed='true']:not([data-kind='song']) {
		background: color-mix(in srgb, var(--color-gold) 16%, transparent);
		color: var(--color-gold);
		box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--color-gold) 45%, transparent);
	}

	/* What is touched while listening, docked where the thumb is: the line being sung and the timing. */
	.studio-console {
		position: sticky;
		bottom: 0;
		z-index: 1;
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		gap: 0.5rem;
		border-radius: var(--radius-panel);
		border: 1px solid var(--color-haze);
		background: color-mix(in srgb, var(--color-surface-0) 92%, transparent);
		backdrop-filter: blur(12px);
		padding: 0.625rem;
		box-shadow: 0 -12px 24px -12px var(--color-dark-0);
	}

	.studio-button:focus-visible,
	.studio-row:focus-visible,
	.studio-switch button:focus-visible {
		outline: 2px solid var(--color-primary-200);
		outline-offset: 2px;
	}

	@media (min-width: 640px) {
		.studio-field {
			font-size: 0.875rem;
		}
		.studio-switch button {
			flex: none;
		}
	}
	@media (min-width: 1024px) {
		.studio-box {
			height: 28rem;
		}
	}
	@media (pointer: coarse) {
		.studio-button,
		.studio-row {
			min-height: 2.75rem;
		}
	}
</style>
