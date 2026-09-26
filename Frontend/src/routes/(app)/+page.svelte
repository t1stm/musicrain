<script lang="ts">
	import { goto, replaceState } from '$app/navigation';
	import { page } from '$app/state';
	import { resolve } from '$app/paths';
	import { onMount } from 'svelte';
	import type { PageData } from './$types';
	import type { SearchResult } from '$states/search.svelte';
	import queue from '$states/queue.svelte';
	import session from '$states/session.svelte';
	import { getRecentlyPlayed } from '$lib/recentlyPlayed';
	import {
		AudioApiError,
		findQueryType,
		getLocalVariant,
		getRandomSongs,
		isPlaylist,
		streamArtistLocal,
		streamRandomSongs
	} from '$requests/songs';
	import { streamSearch } from '$requests/search';
	import type { LocalVariant } from '$requests/songs';
	import { convertTimeSpanStringToSeconds, getTimeString, heroArtist } from '$lib';
	import { closeOnBack, pushPageState } from '$lib/backWatcher.svelte';
	import { at, record, type Roll } from '$lib/rollHistory';
	import { SliderInteractions } from '$lib/sliderInteractions.svelte.js';
	import Song from '$components/home/song/Song.svelte';
	import SongSkeleton from '$components/home/song/SongSkeleton.svelte';
	import Skeleton from '$components/Skeleton.svelte';
	import ArtistLink from '$components/ArtistLink.svelte';

	import { ArrowPath, FolderOpen, Icon, Link, Play } from 'svelte-hero-icons';

	const { data }: { data: PageData } = $props();

	let hero = $state<SearchResult | null>(null);
	let heroLoading = $state(true);
	// Slots, not lists: each track lands in its own place and every slot the stream
	// has not reached yet keeps its placeholder. The count is what was asked for;
	// the end of the stream settles what it really is.
	let curated = $state<(SearchResult | null)[]>(Array(30).fill(null));
	// the artist endpoint takes no count, so a row's worth is the guess
	let artistSongs = $state<(SearchResult | null)[]>(Array(6).fill(null));
	let recentlyPlayed = $state<SearchResult[]>([]);
	let rolling = $state(false);
	let rollingPicks = $state(false);
	// The roll's odds, not a level: both sources are always drawn from, so the bar
	// is always full and the handle is the seam between the two. The API defaults
	// to 40 when the param is absent. 5% detents keep the readout tidy.
	// The handle's position is the library share — it is the left territory, so
	// the pointer and the seam move together and ArrowRight means "more library".
	const odds = new SliderInteractions(5, 60);
	let libraryPercent = $derived(Math.round(odds.percentage / 5) * 5);
	let youTubePercent = $derived(100 - libraryPercent);
	let pastedQuery = $state('');
	let resolving = $state(false);
	let pasteMessage = $state('');
	let pasteError = $state('');
	// The tape: every track this paste has put in the queue, in the order it landed. It stays after
	// the stream ends — it is the receipt for what was added, and cancelling keeps what it printed.
	let pasteTracks = $state<SearchResult[]>([]);
	let pasteAbort: AbortController | null = null;
	let tape = $state<HTMLDivElement | null>(null);
	// Counts come from the one 200-track sample the page already loads, so the
	// heaviest names in the library surface first without a second request.
	// ponytail: the sample is the heaviest request on the page and exists only for
	// this tally. Drop it for an artist-count endpoint once the API has one.
	let artists = $state<[string, number][]>([]);
	let artistsLoading = $state(true);
	const artistUrl = (name: string) => `${resolve('/artist')}?term=${encodeURIComponent(name)}`;
	let heroDuration = $derived(hero ? getTimeString(convertTimeSpanStringToSeconds(hero.duration)) : '');
	let heroInLibrary = $derived(hero?.id.startsWith('audio://') ?? false);
	// ponytail: the result contract carries no format field. For library tracks the
	// raw file's extension is the only signal there is; YouTube results have none
	// to give, so they get no tag. Drop this once the API returns a real format.
	const formatOf = (song: SearchResult | null) =>
		(song?.id.startsWith('audio://') && song.contentUrl?.split('?')[0].match(/\.(\w{2,4})$/)?.[1].toUpperCase()) ||
		'';
	let heroFormat = $derived(formatOf(hero));

	// What the library says about this roll, and which button is waiting on an answer.
	// Nothing is shown until a press: the hero looks exactly as it did.
	let variant = $state<LocalVariant | null>(null);
	let pending = $state<'play' | 'queue' | null>(null);
	// Which roll is on screen. A roll is kept rather than overwritten, so back is an undo
	// of the button; history carries the index and the tracks stay here. It is also what
	// keeps a suggestion from a roll the user has since backed out of off the hero.
	let shown: Roll | null = null;
	// The roll the page loaded with, until the first roll is pushed on top of it.
	let landed: Roll | null = null;
	let promptFirst = $state<HTMLButtonElement | null>(null);
	let pressed: HTMLElement | null = null;

	let verb = $derived(pending === 'play' ? 'Play' : 'Add');
	// One word for the library side rather than a synonym table: "the original" reads
	// right against every rendition tag, and the tag itself names the other button.
	let rendition = $derived(variant?.youTubeTags.join(' ') ?? '');
	let delta = $derived(variant?.durationDeltaSeconds ?? 0);
	let deltaText = $derived(delta === 0 ? '' : `${Math.abs(delta)}s ${delta < 0 ? 'shorter' : 'longer'}`);
	let eyebrow = $derived(
		variant?.match === 'variant'
			? 'The original is in your library'
			: variant?.match === 'weak'
				? 'Possibly the same track'
				: 'In your library'
	);
	let variantLine = $derived(
		variant
			? [
					`${variant.result.name} — ${variant.result.artist}`,
					formatOf(variant.result),
					getTimeString(convertTimeSpanStringToSeconds(variant.result.duration)),
					variant.match === 'weak' ? deltaText : ''
				]
					.filter(Boolean)
					.join(' · ')
			: ''
	);

	$effect(() => {
		if (pending && promptFirst) promptFirst.focus();
	});

	// The fork is a layer over the hero: back dismisses it and leaves the roll alone.
	closeOnBack(() => pending !== null, close);

	/**
	 * Puts a roll on screen. The page's arrays are reactive proxies of the roll's, so the
	 * roll is pointed back at them — a stream still filling this roll then writes where the
	 * page reads, whether or not the roll was on screen when it started.
	 */
	function show(roll: Roll) {
		shown = roll;
		hero = roll.hero;
		artistSongs = roll.artistSongs;
		curated = roll.picks;
		variant = roll.variant;
		pending = null;
		heroLoading = false;
		roll.artistSongs = artistSongs;
		roll.picks = curated;
	}

	// Back and forward through the rolls. `at` returns null for an index this page load
	// never drew — a reload — and the page then keeps the roll it loaded with.
	$effect(() => {
		const roll = at(page.state.home ?? -1);
		if (roll && roll !== shown) show(roll);
	});

	/**
	 * Puts a roll on screen with a history entry of its own, so back brings back the one it
	 * displaced. The roll the page landed on gets its index here rather than at mount: the
	 * router is not initialized yet when `onMount` runs, and `replaceState` throws.
	 */
	function pushRoll(roll: Roll) {
		if (landed && page.state.home === undefined) {
			replaceState('', { ...page.state, home: record(landed) });
		}
		show(roll);
		pushPageState({ home: record(roll) });
	}

	/**
	 * Each track lands in its own slot as the response produces it. The slots the
	 * stream never reaches are dropped at its end — including all of them, which is
	 * how a failed request becomes an empty section rather than a page of shapes.
	 */
	async function fill(slots: (SearchResult | null)[], stream: AsyncIterable<SearchResult>) {
		let index = 0;
		try {
			for await (const song of stream) slots[index++] = song;
		} finally {
			slots.length = index;
		}
	}

	// The tally is sorted by count, so filling it per track would reshuffle the whole
	// cloud two hundred times. This one is drained first and shown once.
	async function countArtists() {
		const tally: Record<string, number> = {};
		try {
			for await (const song of data.librarySongs) {
				if (song.artist) tally[song.artist] = (tally[song.artist] ?? 0) + 1;
			}
		} catch {
			// whatever was counted before it died is still worth showing
		}
		artists = Object.entries(tally).sort(
			([nameA, countA], [nameB, countB]) => countB - countA || nameA.localeCompare(nameB)
		);
		artistsLoading = false;
	}

	// The requests are already in flight from the load function; this is where what
	// they carry starts landing in the page. In onMount rather than beside it, so the
	// slot arrays are read when they are handed over rather than captured at init.
	onMount(() => {
		recentlyPlayed = getRecentlyPlayed();
		countArtists();

		// Coming back to this page lands on the history entry it left on, and that entry's
		// roll is still here. Put it back rather than replacing it with the load's fresh one.
		const previous = at(page.state.home ?? -1);
		if (previous) {
			show(previous);
			return;
		}

		// The page arrives as its first roll, on the entry that brought it here — so back
		// from the first roll leaves the page, as it always did.
		const first: Roll = { hero: null, artistSongs, picks: curated, variant: null };
		shown = first;
		landed = first;

		data.hero.then((song) => {
			first.hero = song;
			if (shown === first) {
				hero = song;
				heroLoading = false;
			}
			if (song) lookUpVariant(song, first);
		});
		fill(curated, data.picks).catch(() => {});
		data.artistSongs
			.then((stream) => {
				if (stream) return fill(artistSongs, stream);
				artistSongs.length = 0;
			})
			.catch(() => {});
	});

	async function lookUpVariant(song: SearchResult, roll: Roll) {
		// A suggestion that fails to load is a hero with no prompt, never an error.
		const found = await getLocalVariant(song, fetch).catch(() => null);
		roll.variant = found;
		// A fast second roll must not have the first roll's suggestion land on it. The
		// lookup is not awaited by rollAgain, so the roll stays as quick as it was.
		if (shown === roll) variant = found;
	}

	function imageFallback(event: Event) {
		const image = event.currentTarget as HTMLImageElement;
		if (!image.src.endsWith('/empty.png')) image.src = '/empty.png';
	}

	async function rollAgain() {
		if (rolling) return;
		rolling = true;
		try {
			const nextHero = (await getRandomSongs(fetch, 1, youTubePercent / 100))[0] ?? null;
			// A roll of its own: a new hero over fresh slots, the same picks below it, and a
			// history entry, so back puts the roll this one displaced back on screen. A fill
			// still running from the last roll writes into that roll's own array.
			const roll: Roll = {
				hero: nextHero,
				artistSongs: Array(6).fill(null),
				picks: curated,
				variant: null
			};
			pushRoll(roll);
			if (nextHero) {
				lookUpVariant(nextHero, roll);
				fill(roll.artistSongs, streamArtistLocal(heroArtist(nextHero.artist), fetch)).catch(() => {});
			} else roll.artistSongs.length = 0;
		} finally {
			rolling = false;
		}
	}

	async function rollPicks() {
		if (rollingPicks) return;
		rollingPicks = true;
		// The same roll's hero over a new set of picks, and its own entry: back brings the
		// set this one threw away back.
		const roll: Roll = { hero, artistSongs, picks: Array(30).fill(null), variant };
		pushRoll(roll);
		try {
			await fill(roll.picks, streamRandomSongs(fetch, 30, youTubePercent / 100));
		} catch {
			// fill's own end has already trimmed the row to what arrived
		} finally {
			rollingPicks = false;
		}
	}

	function press(which: 'play' | 'queue', event: MouseEvent) {
		if (!hero) return;
		if (variant) {
			pressed = event.currentTarget as HTMLElement;
			pending = which;
			return;
		}
		act(which, hero);
	}

	function choose(useVariant: boolean) {
		const song = useVariant ? variant?.result : hero;
		const which = pending;
		close();
		if (song && which) act(which, song);
	}

	function act(which: 'play' | 'queue', song: SearchResult) {
		// In a room queue.add sends `add <id>`, so swapping the id means the room
		// streams a local file instead of every listener going out to YouTube.
		if (which === 'play') queue.playNow(song);
		else queue.add(song);
	}

	/** Back and Escape both restore the row, and the focus to the button that was pressed. */
	function close() {
		pending = null;
		pressed?.focus();
		pressed = null;
	}

	async function resolvePaste() {
		const value = pastedQuery.trim();
		if (!value || resolving) return;

		resolving = true;
		pasteError = '';
		pasteMessage = '';
		pasteTracks = [];

		// Threaded through the fetcher both request helpers already take, so Cancel closes the
		// response and the API stops looking the rest of the playlist up — see PASTE_STREAM_PLAN.md.
		const controller = new AbortController();
		pasteAbort = controller;
		const signalled: typeof fetch = (input, init) => fetch(input, { ...init, signal: controller.signal });

		try {
			const resolved = await findQueryType(value, signalled);
			if (resolved.kind === 'search') {
				const searchUrl = `${resolve('/search')}?term=${encodeURIComponent(resolved.query)}`;
				await goto(searchUrl);
				return;
			}
			if (!isPlaylist(resolved)) {
				play(resolved.result);
				pasteMessage = `Playing ${resolved.result.name}.`;
			} else {
				// A playlist resolution carries no tracks: the canonical query goes back to Search,
				// which runs the same lookup and yields each track as it resolves. One at a time is
				// the point — a Spotify playlist is a library-then-YouTube lookup per track.
				for await (const track of streamSearch(resolved.query, signalled)) play(track);
				pasteMessage =
					pasteTracks.length === 0
						? 'That playlist did not contain any playable tracks.'
						: `Added ${pasteTracks.length} tracks.`;
			}
			pastedQuery = '';
		} catch (error) {
			// Cancelling is not a failure. What already landed stays in the queue — pulling a track
			// out from under the one now playing is the worse surprise.
			if (controller.signal.aborted) pasteMessage = `Stopped. ${pasteTracks.length} tracks added.`;
			else pasteError = error instanceof AudioApiError ? error.message : 'Could not resolve that link. Please try again.';
		} finally {
			resolving = false;
			pasteAbort = null;
		}
	}

	/** The first track of a paste starts playing; the rest queue behind it. Both land on the tape. */
	function play(song: SearchResult) {
		if (pasteTracks.length === 0) queue.playNow(song);
		else queue.add(song);
		pasteTracks.push(song);
	}

	// The newest row is the one worth seeing, and the tape is shorter than a playlist. An effect, not
	// a line in `play`: the row has to exist before it can be scrolled to.
	$effect(() => {
		if (pasteTracks.length) tape?.scrollTo({ top: tape.scrollHeight });
	});

</script>

<svelte:head><title>musicrain</title></svelte:head>

<div class="page gap-10 p-4 sm:p-6 sm:pb-28">
	<section
		class="overflow-hidden rounded-panel border border-haze bg-surface-100 bg-[radial-gradient(120%_140%_at_8%_20%,color-mix(in_srgb,var(--color-primary-0)_26%,transparent),transparent_62%)]"
	>
		{#if hero}
			<div class="grid gap-5 p-4 sm:grid-cols-[11rem_1fr] sm:items-center sm:gap-6 sm:p-6 lg:grid-cols-[15rem_1fr] lg:gap-8 lg:p-8">
				<img
					src={hero.thumbnailUrl ?? '/empty.png'}
					alt=""
					class="aspect-square w-full max-w-32 rounded-row object-cover sm:max-w-44 lg:max-w-60"
					onerror={imageFallback}
				/>
				<div class="flex min-w-0 flex-col justify-center">
					<p class="eyebrow text-primary-500">The roll</p>
					<h1
						class="mt-2 font-display text-xl font-light leading-tight tracking-tight text-chalk sm:text-2xl lg:text-3xl"
					>
						{hero.name}
					</h1>
					<ArtistLink artist={hero.artist} class="mt-1 w-fit text-fog" />
					<p class="mt-2 flex flex-wrap items-center gap-2 font-mono text-[0.68rem] uppercase tracking-[0.13em] text-fog">
						<span>{heroDuration}{heroFormat ? ` · ${heroFormat === 'FLAC' ? 'FLAC available' : heroFormat}` : ''}</span>
						{#if variant}
							<!-- The lookup lands after the roll does, so this is the only thing that says a
							     prompt is waiting behind the buttons. Gold is the library, and an uncertain
							     match keeps the hairline in haze the way the prompt itself does. -->
							<span
								class="tag rounded-art border px-1.5 py-0.5 {variant.match === 'weak'
									? 'border-haze text-fog'
									: 'border-gold/45 text-gold'}">Alternative found</span
							>
						{/if}
					</p>
					{#if pending && variant}
						<!-- A fork in a row of buttons, not a dialog: no overlay, no dimmed page, no focus trap. -->
						<div
							class="prompt mt-4 max-w-md rounded-row border p-3 {variant.match === 'weak'
								? 'border-haze'
								: 'border-gold/45'}"
							role="group"
							aria-label="Choose which copy of {hero.name} to {verb.toLowerCase()}"
						>
							<p class="eyebrow {variant.match === 'weak' ? 'text-fog' : 'text-gold'}">{eyebrow}</p>
							<p class="mt-1.5 text-sm text-fog">
								{variantLine}{#if variant.match === 'variant' && rendition}
									· this one is <span class="text-ember">{rendition}</span>
								{/if}
							</p>
							<div class="mt-3 flex flex-wrap gap-2">
								<button
									bind:this={promptFirst}
									type="button"
									class="min-h-11 rounded-row bg-primary-600 px-3 py-2 text-sm font-semibold text-white"
									onclick={() => choose(true)}
									>{verb} the {variant.match === 'variant' ? 'original' : 'library copy'}</button
								>
								<button
									type="button"
									class="min-h-11 rounded-row border border-haze px-3 py-2 text-sm font-semibold text-chalk hover:bg-surface-200"
									onclick={() => choose(false)}
									>{verb} the {variant.match === 'variant' && rendition ? rendition : 'YouTube'} one</button
								>
							</div>
						</div>
					{:else}
						<div class="mt-4 flex flex-wrap gap-2">
							<!-- in a room nobody plays anything directly, so queueing is the only verb and it takes the accent -->
							{#if !session.inRoom}
								<button
									type="button"
									class="inline-flex min-h-11 items-center gap-2 rounded-row bg-primary-600 px-3 py-2 text-sm font-semibold text-white"
									onclick={(event) => press('play', event)}><Icon src={Play} mini size="16" /> Play</button
								>
							{/if}
							<button
								type="button"
								class="min-h-11 rounded-row px-3 py-2 text-sm font-semibold {session.inRoom
									? 'bg-primary-600 text-white'
									: 'border border-haze text-chalk hover:bg-surface-200'}"
								onclick={(event) => press('queue', event)}>Add to queue</button
							>
							<button
								type="button"
								class="inline-flex min-h-11 items-center gap-2 rounded-row border border-haze px-3 py-2 text-sm font-semibold text-chalk hover:bg-surface-200 disabled:opacity-60"
								onclick={rollAgain}
								disabled={rolling}
								><Icon src={ArrowPath} mini size="16" class={rolling ? 'animate-spin' : ''} /> Roll again</button
							>
						</div>
					{/if}
					<p class="sr-only" aria-live="polite">{pending && variant ? `${eyebrow}. ${variantLine}` : ''}</p>
					<!-- The odds and the outcome in one object: the seam is where you set the
					     split, the lit dot is the side this roll actually came from. -->
					<div class="mt-5 max-w-xs sm:max-w-sm">
						<div
							class="mb-2 flex items-center justify-between font-mono text-[0.62rem] uppercase tracking-[0.13em]"
						>
							<span class="flex items-center gap-1.5 {heroInLibrary ? 'text-gold' : 'text-fog'}">
								<span
									class="size-1.5 rounded-full bg-gold transition-opacity duration-300"
									class:opacity-0={!heroInLibrary}
								></span>
								Library {libraryPercent}%
							</span>
							<span class="flex items-center gap-1.5 {heroInLibrary ? 'text-fog' : 'text-ember'}">
								{youTubePercent}% YouTube
								<span
									class="size-1.5 rounded-full bg-ember transition-opacity duration-300"
									class:opacity-0={heroInLibrary}
								></span>
							</span>
						</div>
						<div
							class="group relative flex h-7 cursor-pointer touch-none items-center rounded-row outline-surface-300 focus-visible:outline-4"
							role="slider"
							tabindex="0"
							aria-label="Share of each roll drawn from your library, in percent"
							aria-valuemin="0"
							aria-valuemax="100"
							aria-valuenow={libraryPercent}
							aria-valuetext="{libraryPercent}% library, {youTubePercent}% YouTube"
							onfocusin={odds.enter}
							onfocusout={odds.leave}
							onpointerenter={odds.enter}
							onpointerleave={odds.leave}
							onpointerdown={odds.pointerDown}
							onpointermove={odds.pointerMove}
							onpointerup={odds.pointerUp}
							onpointercancel={odds.pointerUp}
							onkeydown={odds.keydown}
						>
							<div
								class="flex h-2 w-full overflow-hidden rounded-row transition-[height] duration-150 group-hover:h-3 group-focus-visible:h-3"
							>
								<div class="bg-gold transition-[width] duration-100" style:width={libraryPercent + '%'}></div>
								<div class="flex-1 bg-ember"></div>
							</div>
							<!-- ringed in the page ground so the seam stays legible against either side -->
							<div
								class="pointer-events-none absolute top-1/2 h-4 w-[3px] -translate-x-1/2 -translate-y-1/2 rounded-full bg-chalk shadow-[0_0_0_2px_var(--color-dark-0)] transition-[left,height] duration-100 group-hover:h-5 group-focus-visible:h-5"
								style:left={libraryPercent + '%'}
							></div>
						</div>
						<p class="sr-only">This roll came from {heroInLibrary ? 'the library' : 'YouTube'}.</p>
					</div>
				</div>
			</div>
		{:else if heroLoading}
			<!-- the same grid, so the hero lands in the space it was already holding -->
			<div
				class="grid gap-5 p-4 sm:grid-cols-[11rem_1fr] sm:items-center sm:gap-6 sm:p-6 lg:grid-cols-[15rem_1fr] lg:gap-8 lg:p-8"
				aria-busy="true"
			>
				<Skeleton class="aspect-square w-full max-w-32 rounded-row sm:max-w-44 lg:max-w-60" />
				<div class="flex min-w-0 flex-col justify-center">
					<Skeleton class="h-3 w-20" />
					<Skeleton class="mt-3 h-7 w-3/4 max-w-md" />
					<Skeleton class="mt-2 h-4 w-40" />
					<Skeleton class="mt-3 h-3 w-32" />
					<div class="mt-4 flex flex-wrap gap-2">
						<Skeleton class="h-11 w-24 rounded-row" />
						<Skeleton class="h-11 w-32 rounded-row" />
						<Skeleton class="h-11 w-32 rounded-row" />
					</div>
					<div class="mt-5 max-w-xs sm:max-w-sm">
						<div class="mb-2 flex items-center justify-between">
							<Skeleton class="h-3 w-24" />
							<Skeleton class="h-3 w-24" />
						</div>
						<Skeleton class="h-2 w-full rounded-row" />
					</div>
				</div>
			</div>
			<p class="sr-only" aria-live="polite">Loading the roll.</p>
		{:else}
			<p class="p-6 text-fog">The roll is resting for a moment. Try again shortly.</p>
		{/if}
	</section>

	<section class="rounded-panel border border-haze bg-surface-0/50 p-4 sm:p-5">
		<h2 class="eyebrow mb-3 flex items-center gap-2">
			<Icon src={Link} mini size="14" class="text-gold" />
			{#if resolving || pasteTracks.length > 0}
				Adding to the queue
				<span class="font-mono text-primary-400" aria-live="polite">{pasteTracks.length}</span>
			{:else}
				Paste a link or ID
			{/if}
		</h2>
		<form
			class="flex flex-col gap-2 sm:flex-row"
			onsubmit={(event) => {
				event.preventDefault();
				resolvePaste();
			}}
		>
			<input
				bind:value={pastedQuery}
				class="min-w-0 flex-1 rounded-row border-haze bg-dark-0 text-chalk placeholder:text-fog focus:border-primary-0 focus:ring-primary-0"
				placeholder="YouTube link, playlist, or audio:// ID"
				aria-label="Paste a link or audio ID"
			/>
			<!-- One action slot, one action: the label always names what pressing it does. Stopping is
			     the interrupting side of the fork, so it takes ember and a hairline, never the fill. -->
			{#if resolving}
				<button
					type="button"
					class="min-h-11 rounded-row border border-ember px-4 py-2 text-sm font-semibold text-ember"
					onclick={() => pasteAbort?.abort()}>Cancel</button
				>
			{:else}
				<button type="submit" class="min-h-11 rounded-row bg-primary-600 px-4 py-2 text-sm font-semibold text-white"
					>Play</button
				>
			{/if}
		</form>
		{#if pasteTracks.length > 0}
			<!-- The tape. A playlist is an ordered sequence, so the number is information: it is the
			     position the track just took in the queue. The gutter rains while the stream is open —
			     the app's own "this is still filling" element, from the seek bar's buffer gauge. -->
			<div class="relative mt-3 pl-4">
				<span class="absolute inset-y-0 left-0 w-px overflow-hidden bg-haze">
					{#if resolving}<span class="rain-streak"></span>{/if}
				</span>
				<div bind:this={tape} class="max-h-56 overflow-y-auto pr-1">
					{#each pasteTracks as track, index (track.id + index)}
						<div class="tape-row flex min-w-0 items-baseline gap-2.5 py-1 text-sm">
							<span class="font-mono text-[0.68rem] text-surface-400">{String(index + 1).padStart(2, '0')}</span>
							<span class="min-w-0 flex-1 truncate">
								<span class="text-chalk">{track.name}</span>
								<span class="text-fog"> — {track.artist}</span>
							</span>
							{#if index === 0}
								<span class="eyebrow shrink-0 text-gold">now</span>
							{:else}
								<span class="shrink-0 font-mono text-[0.68rem] text-surface-400"
									>{getTimeString(convertTimeSpanStringToSeconds(track.duration))}</span
								>
							{/if}
						</div>
					{/each}
				</div>
			</div>
		{/if}
		{#if pasteError}<p class="mt-2 text-sm text-gold">{pasteError}</p>{/if}
		{#if pasteMessage}<p class="mt-2 text-sm text-fog">{pasteMessage}</p>{/if}
	</section>

	{#if artistSongs.length > 0}
		<section>
			<h2 class="eyebrow mb-3">More from this artist</h2>
			<!-- keyed by slot, not by track: the slot is what persists while the row fills -->
			<div class="flex gap-4 overflow-x-auto pb-2" aria-busy={artistSongs.includes(null)}>
				{#each artistSongs as song, slot (slot)}
					{#if song}<Song {song} />{:else}<SongSkeleton />{/if}
				{/each}
			</div>
		</section>
	{/if}
	{#if recentlyPlayed.length > 0}
		<section>
			<h2 class="eyebrow mb-3">Back where you left off</h2>
			<div class="flex gap-4 overflow-x-auto pb-2">
				{#each recentlyPlayed as song (song.id)}<Song {song} />{/each}
			</div>
		</section>
	{/if}

	<section>
		<div class="mb-3 flex items-center gap-3">
			<h2 class="eyebrow">Artists in the library</h2>
			<!-- The tags above are the 200-track sample's heaviest names; this is the whole
			     thing, in the folders it is actually stored in. -->
			<a
				href={resolve('/browse')}
				class="inline-flex min-h-9 items-center gap-1.5 rounded-[5px] border border-haze px-2.5 py-1 text-xs font-semibold text-chalk hover:border-gold hover:text-gold"
				><Icon src={FolderOpen} mini size="14" /> Browse by folder</a
			>
		</div>
		{#if artistsLoading}
			<div class="flex flex-wrap gap-2" aria-busy="true">
				<!-- uneven widths so it reads as a wrapped line of names, not a grid -->
				{#each ['w-20', 'w-32', 'w-24', 'w-36', 'w-28', 'w-20', 'w-32', 'w-24', 'w-36', 'w-28', 'w-24', 'w-32'] as width, slot (slot)}
					<Skeleton class="h-9 rounded-full {width}" />
				{/each}
			</div>
			<p class="sr-only" aria-live="polite">Counting the artists in the library.</p>
		{:else}
			<div class="flex flex-wrap gap-2">
				{#each artists as [artist, count] (artist)}
					<a
						href={artistUrl(artist)}
						class="inline-flex min-h-9 items-center rounded-full border border-haze bg-surface-0 px-3 py-1 text-sm text-chalk transition-colors hover:border-gold hover:text-gold"
						>{artist}<span class="ml-1.5 font-mono text-[0.68rem] text-fog">{count}</span></a
					>
				{/each}
			</div>
		{/if}
	</section>

	<section>
		<div class="mb-3 flex items-center gap-3">
			<h2 class="eyebrow">(Curated) Picks</h2>
			<button
				type="button"
				class="inline-flex min-h-9 items-center gap-1.5 rounded-[5px] border border-haze px-2.5 py-1 text-xs font-semibold text-chalk hover:bg-surface-200 disabled:opacity-60"
				onclick={rollPicks}
				disabled={rollingPicks}
				><Icon src={ArrowPath} mini size="14" class={rollingPicks ? 'animate-spin' : ''} /> Roll again</button
			>
		</div>
		<div
			class="grid grid-flow-col-dense grid-rows-2 gap-4 overflow-x-auto p-2 sm:gap-6"
			aria-busy={curated.includes(null)}
		>
			{#each curated as song, slot (slot)}
				{#if song}<Song {song} />{:else}<SongSkeleton />{/if}
			{/each}
		</div>
	</section>
</div>

<style>
	/* The row's own hover timing. ponytail: opacity only — the prompt replaces a
	   button row of near-identical height, so an animated height buys nothing. The
	   water/ripple idea belongs to landing in the queue and is not spent twice.
	   A tape row lands on the same reveal: one entrance for the whole page, and the
	   stagger between rows is the network's, which is the only honest one. */
	.prompt,
	.tag,
	.tape-row {
		animation: reveal 150ms ease-out;
	}

	@keyframes reveal {
		from {
			opacity: 0;
			transform: translateY(-4px);
		}
	}

	@media (prefers-reduced-motion: reduce) {
		.prompt,
		.tag,
		.tape-row {
			animation: none;
		}
	}
</style>
