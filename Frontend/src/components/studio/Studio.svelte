<script lang="ts">
	/*
	 * The lyrics studio, for staff with the studio permission. The back end is the lirika pod at
	 * /Audio/Lirika (Tools/Lirika's studio.py), which asks Dom who the token belongs to. Pick a library song, start from textove's words (or your last edit),
	 * edit them, set the aligner for the song, regenerate, then fix lines by hand. Every keystroke in the LRC goes to the
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
	import { staffCall } from '$requests/staff';
	import { streamJson } from '$lib/streamJson';
	import { audioApi } from '$lib/discord';
	import type { Lyrics, LyricLine } from '$requests/lyrics';
	import { seekTo } from '$lib/seek';

	const LIRIKA = `${audioApi}/Lirika`;

	type Candidate = { source: string; label: string; text: string; url?: string };
	type Flagged = { start: number; end: number; text: string; flags: string[] };
	type Ear = 'song' | 'lead' | 'vocals';
	type Generated = { lrc: string; lines: Flagged[]; songFlags: string[] };
	type Generation = { stage: string } | { result: Generated } | { error: { message: string } };
	type Setting = string | number | boolean;
	// One row of studio.py's KNOBS: choices draw a select, the rest a range from min to max.
	type Knob = {
		name: string;
		label: string;
		hint: string;
		needs: string[];
		default: Setting;
		choices?: { value: Setting; label: string }[];
		min?: number;
		max?: number;
		step?: number;
	};

	// What each stem is: what to listen for when a line lands wrong.
	const EARS: { kind: Ear; label: string; role: string }[] = [
		{ kind: 'song', label: 'Song', role: 'The song as listeners hear it.' },
		{ kind: 'lead', label: 'Lead vocal', role: 'The singer out front, without backing voices or duet partners.' },
		{ kind: 'vocals', label: 'All voices', role: 'Every voice, without the instruments.' }
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
	// The stages of this Regenerate that are over, with the seconds each took. `busy` is the one under way.
	let steps = $state<{ label: string; took: number }[]>([]);
	let note = $state('');
	let link = $state('');
	let loadedFor = '';
	// The LRC as Lirika wrote it or as last saved: a save that differs from it marks the file edited.
	let untouched = '';
	let editor = $state<HTMLTextAreaElement | null>(null);
	// Below lg the two boxes take turns; side by side above it.
	let pane = $state<'source' | 'lrc'>('source');

	let knobs = $state<Knob[]>([]);
	let settings = $state<Record<string, Setting>>({});
	const defaults = () => Object.fromEntries(knobs.map((knob) => [knob.name, knob.default]));
	// Closed, the settings still say the language (the table's first knob) and whether others are turned.
	let language = $derived(knobs[0]?.choices?.find((choice) => choice.value === settings[knobs[0].name])?.label ?? '');
	let turned = $derived(knobs.filter((knob) => settings[knob.name] !== knob.default).length);

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
			: 'Stems arrive with Regenerate, which separates the ones its settings use.'
	);

	// ── For the sheet's decoration only: none of it writes to the text or the logic above ──────
	// Every stamp on each line of the LRC box, in the box's own line order.
	let lineStamps = $derived(lrc.split('\n').map((line) => [...line.matchAll(STAMP)].map(seconds)));
	// The line of the box the playhead is in, for the gold band behind it.
	let nowLine = $derived(now ? lineStamps.findIndex((stamps) => stamps.includes(now.at)) : -1);
	// Lines Lirika flagged, for the ember marks in the gutter. A stamp is written to the hundredth.
	let flaggedLines = $derived(
		new Set(lineStamps.flatMap((stamps, index) =>
			stamps.some((at) => flagged.some((line) => Math.abs(line.start - at) < 0.006)) ? [index] : []))
	);
	let backdrop = $state<HTMLDivElement | null>(null);
	// How long the current wait or stage has run: a minute of separating reads as work, not as a hang.
	let waited = $state(0);
	$effect(() => {
		if (!busy) return;
		const since = Date.now();
		waited = 0;
		const tick = setInterval(() => (waited = Math.floor((Date.now() - since) / 1000)), 1000);
		return () => clearInterval(tick);
	});
	const elapsed = (s: number) => `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;

	// The gate has already turned away a 401 or a 403, so a failure here is the pod itself: not running,
	// or nginx answering 502 for it.
	onMount(async () => {
		try {
			knobs = await call<Knob[]>('/knobs');
			settings = defaults();
			server = 'up';
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

	// Whatever plays by now, even a track with no file: the draft must not outlive the studio.
	onDestroy(() => lyrics.showDraft(track, null));

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
			const blob = await staffCall<Blob>(`${LIRIKA}/stem?id=${encodeURIComponent(id)}&kind=${kind}`, {}, 'blob');
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

	/** A GET, or a POST of `body` as JSON, with the bearer token; throws the pod's own message. */
	function call<T>(path: string, body?: Record<string, unknown>): Promise<T> {
		return staffCall<T>(`${LIRIKA}${path}`, body === undefined ? {} : { method: 'POST', body });
	}

	async function loadTexts(id: string) {
		loadedFor = id;
		lrc = ''; // the last song's words must not become this song's draft, or its Save
		settings = defaults(); // nor its settings this song's
		steps = [];
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
		const id = track;
		const asking = 'Asking the lyrics service…';
		busy = asking;
		steps = [];
		note = '';
		let finished = false;
		try {
			const body = await staffCall<ReadableStream<Uint8Array>>(
				`${LIRIKA}/generate`,
				{ method: 'POST', body: { id, text: source, settings } },
				'body'
			);
			for await (const event of streamJson<Generation>(body)) {
				if (id !== track) return; // another song by now: this one's words must not become its draft
				if ('stage' in event) {
					if (busy !== asking) steps = [...steps, { label: busy, took: waited }];
					busy = event.stage;
				} else if ('error' in event) {
					throw new Error(`${busy}: ${event.error.message}`);
				} else {
					finished = true;
					lrc = event.result.lrc;
					untouched = lrc;
					flagged = event.result.lines.filter((line) => line.flags.length);
					note = event.result.songFlags.join('; ');
					pane = 'lrc';
					void loadStems(id);
				}
			}
			if (!finished) throw new Error(`${busy}: the lyrics service stopped answering.`);
		} catch (error) {
			if (id === track) note = (error as Error).message;
		} finally {
			if (id === track) {
				busy = '';
				steps = [];
			}
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

<header class="studio-head">
	<div class="min-w-0">
		<p class="eyebrow">Lyrics studio</p>
		<h1 class="mt-2 font-display text-xl leading-tight font-extralight tracking-tight text-balance text-chalk sm:text-3xl">
			{current.name ? `${current.artist} — ${current.name}` : 'Play a library song'}
		</h1>
	</div>
	{#if track}
		<!-- the playhead in the stamp's own format, so a line's stamp can be checked against it by eye -->
		<p class="studio-clock" aria-label="Playhead">
			<span class="eyebrow block text-right">Playhead</span>
			<span class="font-mono text-gold">{stamp(audio.currentSeconds)}</span>
		</p>
	{/if}
</header>

<div class="-mt-3 min-h-6" aria-live="polite">
	{#if busy}
		{#if steps.length}
			<ol class="studio-steps">
				{#each steps as step, index (index)}
					<li>
						<span class="min-w-0">{step.label}</span>
						<span class="font-mono">{elapsed(step.took)}</span>
					</li>
				{/each}
			</ol>
		{/if}
		<p class="studio-busy" aria-busy="true">
			<span class="studio-busy-gauge" aria-hidden="true"><span class="rain-streak"></span></span>
			<span class="min-w-0 flex-1">{busy}</span>
			<span class="font-mono text-xs text-fog">{elapsed(waited)}</span>
		</p>
	{:else if server === 'down'}
		<p class="text-sm text-ember">The lyrics service is not running.</p>
	{:else if note}
		<p class="text-sm text-fog">{note}</p>
	{:else if !track && current.id}
		<p class="text-sm text-fog">Only library tracks (audio://) have a file to align.</p>
	{:else}
		<p class="text-sm text-fog">Open the full player to see the draft in the real lyrics pane while you edit.</p>
	{/if}
</div>

<form class="flex gap-2" onsubmit={(event) => { event.preventDefault(); void search(); }}>
	<input class="studio-field min-w-0 flex-1" type="search" enterkeyhint="search" placeholder="Find a library song"
		aria-label="Find a library song" bind:value={query} />
	<button class="studio-button" type="submit">Search</button>
</form>
{#if results.length}
	<ul class="studio-results">
		{#each results as result (result.id)}
			<li>
				<button class="studio-row" onclick={() => { queue.playNow(result); results = []; }}>
					<span class="text-fog">{result.artist}</span> — {result.name}
				</button>
			</li>
		{/each}
	</ul>
{/if}

{#if track}
	<section class="flex flex-wrap items-center gap-x-3 gap-y-2" aria-label="Stems">
		<span class="eyebrow">Listen to</span>
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

	<details class="studio-knobs">
		<summary>
			<span class="eyebrow">Aligner</span>
			<span class="text-xs text-fog">{language}{turned ? ` · ${turned} changed` : ''}</span>
		</summary>
		<div class="studio-knob-grid">
			{#each knobs as knob (knob.name)}
				{@const off = knob.needs.some((name) => settings[name] === false)}
				<div class="studio-knob" data-off={off || undefined}>
					<label class="flex items-baseline justify-between gap-2 text-sm text-chalk" for="knob-{knob.name}">
						{knob.label}
						{#if !knob.choices}<span class="font-mono text-xs text-fog">{settings[knob.name]}</span>{/if}
					</label>
					{#if knob.choices}
						<select class="studio-field" id="knob-{knob.name}" aria-describedby="knob-{knob.name}-hint"
							disabled={off} bind:value={settings[knob.name]}>
							{#each knob.choices as choice (choice.label)}
								<option value={choice.value}>{choice.label}</option>
							{/each}
						</select>
					{:else}
						<input type="range" id="knob-{knob.name}" aria-describedby="knob-{knob.name}-hint"
							min={knob.min} max={knob.max} step={knob.step} disabled={off} bind:value={settings[knob.name]} />
					{/if}
					<p class="text-xs leading-relaxed text-fog" id="knob-{knob.name}-hint">{knob.hint}</p>
				</div>
			{/each}
		</div>
		<button type="button" class="studio-button mt-3" disabled={!turned} onclick={() => (settings = defaults())}>
			Reset to defaults
		</button>
	</details>

	<div class="studio-switch flex lg:hidden" role="group" aria-label="Text to edit">
		<button type="button" aria-pressed={pane === 'source'} onclick={() => (pane = 'source')}>Source text</button>
		<button type="button" aria-pressed={pane === 'lrc'} onclick={() => (pane = 'lrc')}>LRC</button>
	</div>

	<div class="grid gap-6 lg:grid-cols-[minmax(0,5fr)_minmax(0,7fr)]">
		<section class="studio-sheet {pane === 'source' ? 'flex' : 'hidden lg:flex'}" aria-label="Source text">
			<div class="flex items-center gap-2">
				<h2 class="eyebrow max-lg:sr-only">Source text</h2>
				<select class="studio-field min-w-0 flex-1 lg:ml-auto lg:max-w-[70%] lg:flex-none" bind:value={chosen} disabled={!candidates.length}
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
			<textarea class="studio-field studio-text font-mono" bind:value={source} aria-label="Source text"
				spellcheck="false"></textarea>
			<p class="text-xs leading-relaxed text-fog">
				One sung line per line. Order and repeats do not matter to map mode; a missing line, a wrong
				word or a stanza typed as one line do.
			</p>
			<button class="studio-button studio-primary self-start" onclick={regenerate} disabled={!!busy || !source.trim()}>
				Regenerate
			</button>
		</section>

		<section class="studio-sheet {pane === 'lrc' ? 'flex' : 'hidden lg:flex'}" aria-label="LRC">
			<div class="flex items-center gap-2">
				<h2 class="eyebrow max-lg:sr-only">LRC</h2>
				<span class="hidden text-xs text-fog pointer-fine:inline lg:ml-auto">
					<kbd>Alt</kbd>+<kbd>Enter</kbd> line to playhead · <kbd>Alt</kbd>+<kbd>↑</kbd><kbd>↓</kbd> ∓0.1 s
				</span>
				<button class="studio-button ml-auto lg:ml-0" onclick={save} disabled={!lrc.trim()}>Save</button>
			</div>
			<!-- The box is a real textarea over a backdrop that wraps its text identically: the backdrop paints
			     the gold band behind the line at the playhead and the ember marks for flagged lines, and never
			     takes focus or touches the value. -->
			<div class="studio-lrc">
				<div class="studio-field studio-text studio-backdrop font-mono" bind:this={backdrop} aria-hidden="true">
					{#each lrc.split('\n') as line, index (index)}
						<div class="studio-line" data-now={index === nowLine || undefined} data-flagged={flaggedLines.has(index) || undefined}
							>{line || ' '}</div>
					{/each}
				</div>
				<textarea class="studio-field studio-text studio-editor font-mono" bind:value={lrc} bind:this={editor} aria-label="LRC"
					onkeydown={keys} onscroll={() => backdrop && editor && (backdrop.scrollTop = editor.scrollTop)}
					spellcheck="false"></textarea>
			</div>
			{#if flagged.length}
				<h3 class="eyebrow mt-2 text-ember">Flagged by Lirika · {flagged.length}</h3>
				<ul class="flex flex-col">
					{#each flagged as line (line.start)}
						<li>
							<button class="studio-row studio-flag" onclick={() => seekTo(line.start)}>
								<span class="font-mono text-fog">{stamp(line.start)}</span>
								{line.text}
								<span class="block text-xs text-ember">{line.flags.join(', ')}</span>
							</button>
						</li>
					{/each}
				</ul>
			{/if}
		</section>
	</div>

	<section class="studio-console" aria-label="Line at the playhead">
		<button type="button" class="studio-now" disabled={!now} title="Play from this line" onclick={() => now && seekTo(now.at)}>
			<span class="font-mono text-xs text-gold">{now ? stamp(now.at) : '[--:--.--]'}</span>
			<span class="studio-now-text">{now ? now.text || '—' : 'No line at the playhead yet'}</span>
		</button>
		<div class="gap-1.5 max-sm:w-full {pane === 'lrc' ? 'flex' : 'hidden lg:flex'}">
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

<audio bind:this={stemPlayer} src={stemUrl || undefined} preload="auto"></audio>

<style>
	/* ── the page's own vocabulary ──────────────────────────────────────────────────────────────
	   gold is the playhead and nothing else; ember is a flag and nothing else. */

	.studio-head {
		display: flex;
		align-items: flex-end;
		justify-content: space-between;
		gap: 1rem;
	}

	.studio-clock {
		flex-shrink: 0;
		font-size: 1.125rem;
		line-height: 1.2;
		font-variant-numeric: tabular-nums;
	}

	.studio-busy {
		display: flex;
		align-items: center;
		gap: 0.75rem;
		font-size: 0.875rem;
		color: var(--color-chalk);
	}

	/* the app's rain gauge, as a short vertical vial: water falling means work is under way */
	.studio-busy-gauge {
		position: relative;
		width: 4px;
		height: 1.25rem;
		flex-shrink: 0;
		overflow: hidden;
		border-radius: 2px;
		background: var(--color-haze);
	}

	.studio-steps {
		display: flex;
		flex-direction: column;
		gap: 0.125rem;
		margin-bottom: 0.375rem;
		/* under the busy line's text, past its gauge */
		padding-left: calc(4px + 0.75rem);
		font-size: 0.75rem;
		color: var(--color-fog);
	}

	.studio-steps li {
		display: flex;
		justify-content: space-between;
		gap: 0.75rem;
	}

	.studio-knobs {
		border-radius: var(--radius-panel);
		border: 1px solid var(--color-haze);
		background: var(--color-surface-0);
		padding: 0.625rem 0.875rem;
	}

	.studio-knobs summary {
		cursor: pointer;
		color: var(--color-fog);
	}

	.studio-knob-grid {
		display: grid;
		grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr));
		gap: 0.875rem;
		margin-top: 0.75rem;
	}

	.studio-knob {
		display: flex;
		flex-direction: column;
		gap: 0.25rem;
		min-width: 0;
	}

	.studio-knob[data-off] {
		opacity: 0.5;
	}

	.studio-knob input {
		min-height: 2rem;
		accent-color: var(--color-primary-0);
	}

	.studio-field {
		border-radius: var(--radius-row);
		border: 1px solid var(--color-haze);
		background: var(--color-dark-0);
		color: var(--color-chalk);
		padding: 0.5rem 0.75rem;
		/* under 16px, iOS zooms the page in on focus */
		font-size: 1rem;
	}

	.studio-field:focus {
		border-color: var(--color-primary-0);
		outline: none;
	}

	.studio-field:focus-visible,
	.studio-editor:focus {
		box-shadow: 0 0 0 2px color-mix(in srgb, var(--color-primary-0) 45%, transparent);
	}

	.studio-sheet {
		flex-direction: column;
		gap: 0.625rem;
		min-width: 0;
		border-radius: var(--radius-panel);
		border: 1px solid var(--color-haze);
		background: var(--color-surface-0);
		padding: 0.875rem;
	}

	.studio-text {
		height: 45dvh;
		min-height: 16rem;
		line-height: 1.7;
		resize: vertical;
		white-space: pre-wrap;
		overflow-wrap: anywhere;
	}

	/* ── the LRC sheet: a transparent textarea over a backdrop that wraps its text the same way ── */
	.studio-lrc {
		position: relative;
	}

	.studio-lrc > * {
		/* both scroll the same way, so their wrap widths match line for line */
		overflow-y: scroll;
		/* room on the left for the marks, inside both boxes alike */
		padding-left: 1.125rem;
	}

	/* laid exactly over the textarea, resized or not, so both scroll the same range */
	.studio-backdrop {
		position: absolute;
		inset: 0;
		height: auto;
		min-height: 0;
		pointer-events: none;
		color: transparent;
		resize: none;
	}

	.studio-editor {
		position: relative;
		display: block;
		width: 100%;
		background: transparent;
		caret-color: var(--color-chalk);
	}

	.studio-line {
		position: relative;
		margin-inline: -1.125rem -0.75rem;
		padding-inline: 1.125rem 0.75rem;
		transition: background-color 160ms ease;
	}

	.studio-line[data-now] {
		background: color-mix(in srgb, var(--color-gold) 17%, transparent);
	}

	.studio-line[data-now]::before {
		content: '';
		position: absolute;
		inset-block: 0;
		left: 0;
		width: 3px;
		background: var(--color-gold);
	}

	.studio-line[data-flagged]::after {
		content: '';
		position: absolute;
		top: calc(0.85em - 3px);
		left: 0.45rem;
		width: 6px;
		height: 6px;
		border-radius: 50%;
		background: var(--color-ember);
	}

	.studio-button {
		border-radius: var(--radius-row);
		border: 1px solid var(--color-haze);
		background: var(--color-surface-100);
		color: var(--color-chalk);
		padding: 0.4rem 0.9rem;
		font-size: 0.875rem;
		white-space: nowrap;
	}

	.studio-button:hover:enabled {
		border-color: var(--color-surface-300);
		background: var(--color-surface-200);
	}

	.studio-primary {
		border-color: var(--color-primary-0);
		background: var(--color-primary-600);
		font-weight: 600;
		color: white;
	}

	.studio-primary:hover:enabled {
		border-color: var(--color-primary-200);
		background: var(--color-primary-0);
	}

	.studio-button:disabled {
		opacity: 0.5;
	}

	.studio-results {
		display: flex;
		flex-direction: column;
		border-block: 1px solid var(--color-haze);
	}

	.studio-row {
		display: block;
		width: 100%;
		text-align: left;
		border-radius: var(--radius-row);
		padding: 0.4rem 0.6rem;
		font-size: 0.875rem;
		color: var(--color-chalk);
	}

	.studio-row:hover:enabled {
		background: var(--color-surface-100);
	}

	.studio-flag {
		border-left: 2px solid var(--color-ember);
		border-radius: 0 var(--radius-row) var(--radius-row) 0;
		margin-block: 0.125rem;
	}

	kbd {
		font-family: var(--font-mono);
		font-size: 0.6875rem;
		color: var(--color-chalk);
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

	/* The song is the resting state. A stem in its place is outlined: what plays is not what listeners hear. */
	.studio-ears button[aria-pressed='true'] {
		background: var(--color-surface-300);
		color: var(--color-chalk);
	}

	.studio-ears button[aria-pressed='true']:not([data-kind='song']) {
		background: color-mix(in srgb, var(--color-primary-0) 18%, transparent);
		color: var(--color-primary-400);
		box-shadow: inset 0 0 0 1px var(--color-primary-0);
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
		border-top-color: color-mix(in srgb, var(--color-gold) 35%, var(--color-haze));
		background: color-mix(in srgb, var(--color-surface-0) 94%, transparent);
		backdrop-filter: blur(12px);
		padding: 0.625rem;
		box-shadow: 0 -12px 24px -12px var(--color-dark-0);
	}

	.studio-now {
		display: flex;
		min-width: 0;
		flex: 1;
		align-items: baseline;
		gap: 0.625rem;
		border-radius: var(--radius-row);
		padding: 0.35rem 0.5rem;
		text-align: left;
	}

	.studio-now:hover:enabled {
		background: var(--color-surface-100);
	}

	.studio-now-text {
		min-width: 0;
		overflow: hidden;
		text-overflow: ellipsis;
		white-space: nowrap;
		font-size: 1.0625rem;
		color: var(--color-chalk);
	}

	.studio-button:focus-visible,
	.studio-knobs summary:focus-visible,
	.studio-row:focus-visible,
	.studio-now:focus-visible,
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
		.studio-text {
			height: 30rem;
		}
	}

	@media (pointer: coarse) {
		.studio-button,
		.studio-row,
		.studio-now {
			min-height: 2.75rem;
		}
	}

	/* the micro player height: the console keeps the line and the timing, and the sheets get shorter */
	@media (max-height: 320px) {
		.studio-text {
			height: 10rem;
			min-height: 10rem;
		}

		.studio-console {
			padding: 0.375rem;
		}
	}

	@media (prefers-reduced-motion: reduce) {
		.studio-line {
			transition: none;
		}
	}
</style>
