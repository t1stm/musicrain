<script lang="ts">
	import { tick } from 'svelte';
	import { afterNavigate } from '$app/navigation';
	import TrackInfo from './layers/track-info/TrackInfo.svelte';
	import Controls from './layers/controls/Controls.svelte';
	import Volume from './layers/volume/Volume.svelte';
	import Quality from './layers/quality/Quality.svelte';
	import SeekBar from './layers/seek-bar/SeekBar.svelte';
	import { ArrowsRightLeft, ChatBubbleOvalLeft, ChevronDown, ChevronUp, Icon, MusicalNote, QueueList } from 'svelte-hero-icons';
	import Audio from '$components/player/layers/audio/Audio.svelte';
	import Gapless from '$components/player/layers/audio/Gapless.svelte';
	import current from '$states/current.svelte';
	import queue from '$states/queue.svelte';
	import session from '$states/session.svelte';
	import lyrics from '$states/lyrics.svelte';
	import Lyrics from '$components/player/layers/lyrics/Lyrics.svelte';
	import { closeOnBack } from '$lib/backWatcher.svelte';
	import { swipe } from '$lib/swipe';

	type Dock = 'queue' | 'chat' | null;
	let { dock = $bindable<Dock>(null) }: { dock?: Dock } = $props();

	// The full player: the same element, a different shape. A separate component would
	// mount a second <audio> or unmount this one, and both stop the music.
	let full = $state(false);
	closeOnBack(() => full, collapse);

	const phone = () => !window.matchMedia('(min-width: 640px)').matches;

	// One shape becomes the other while the browser morphs between them (app.css): the
	// full shape folds into the bar or grows out of it, and on a phone the sheet slides
	// out from under its edge or back in. Every state change goes in the one callback, so
	// a switch between the full shape and the sheet still closes one layer and opens the
	// other in the same tick — `closeOnBack` hands the history entry over only then.
	// Without view transitions it is the old snap. `kind` names a morph that app.css
	// stages differently, on the root for as long as it runs.
	function morph(update: () => void, kind?: 'open') {
		if (!document.startViewTransition) return update();
		const root = document.documentElement;
		if (kind) root.dataset.morph = kind;
		const transition = document.startViewTransition(async () => {
			update();
			await tick();
		});
		transition.finished.finally(() => delete root.dataset.morph);
		return transition.updateCallbackDone;
	}

	// Whether the full shape rises into place when it opens. Not when it grows out of the
	// bar in a morph: the rise would start it off the foot of the screen, and the morph
	// captures it where it starts.
	let rise = $state(false);

	// A morph draws the full shape live, and an image still decoding at its new size — the
	// sleeve grown from 40px, the ground from nothing — draws as nothing until it ends.
	const decoded = () =>
		Promise.allSettled(
			[...document.querySelectorAll<HTMLImageElement>('#player img')].map((image) => image.decode())
		);

	// micro is a player-only frame with no room for a bigger shape
	async function expand() {
		if (!current.name || window.matchMedia('(max-height: 320px)').matches) return;
		// The other half of `toggle`: on a phone the sheet would sit over the full shape, so
		// the two trade places in one morph — the sheet slides down under its edge as the
		// full shape grows out of the bar.
		rise = !(dock && phone());
		if (rise) return (full = true);
		await decoded();
		return morph(() => {
			full = true;
			dock = null;
		}, 'open');
	}

	// From where the finger left it, on a swipe; the chevron and back take the same way out.
	function collapse() {
		return morph(() => (full = false));
	}

	// The artist and the album in the full shape are links, and the page they open is
	// under it: it folds away so the page is what you see.
	afterNavigate(() => {
		if (full) collapse();
	});

	// The record is the handle, the way it is in every phone's own player: flick it
	// sideways to change track, up to open it out, down to put it away. A tap on the
	// sleeve or the title opens it too — the chevron is a small target for the
	// thing a phone wants most. The controls keep their presses to themselves.
	const gestures = swipe({
		ignore: 'a, button, input, [role=slider], #player-docks, #player-lyrics',
		left: () => queue.nextTrack(),
		right: () => queue.previousTrack(),
		up: expand,
		down: collapse,
		tap: (event) => {
			if ((event.target as Element).closest('#track-info')) expand();
		}
	});

	function toggle(tab: Exclude<Dock, null>) {
		const next = dock === tab ? null : tab;
		// A wide screen holds both — the full player makes room for the sheet beside it.
		// A phone cannot: the sheet is 70dvh of it, so opening one leaves the other.
		if (next && full && phone())
			return morph(() => {
				dock = next;
				full = false;
			});
		dock = next;
	}

	// micro paints the artwork behind everything instead of beside it. No track,
	// no ground — a blown-up placeholder is worse than the plain dark.
	let cover = $derived(current.thumbnail?.length > 0 ? current.thumbnail : '');
	let holdState = $derived(
		session.status === 'holding' ? 'holding' : session.status === 'synced' ? 'playing' : 'idle'
	);

	// Where a queued track actually goes. The button that adds it can be anywhere
	// on the page — this badge is the destination, so it is the thing that has to
	// react. A room's queue grows from other people too, and that is worth seeing.
	let landed = $state<number | null>(null);
	let counted = queue.items.length;
	$effect(() => {
		const count = queue.items.length;
		if (count > counted) landed = Date.now();
		counted = count;
	});
</script>

<!--
  Three shapes, one set of children. Compact docks in flow at the foot of the
  column — which is why no page pads for a player any more. The two row wrappers
  dissolve at `sm` (`display: contents`) and `order` deals the same children into
  the single floating bar. Below `sm` they dissolve too, into app.css's three-row
  phone grid. The phone bar and micro are driven from app.css, not from here:
  `micro:` and `sm:` both match a short wide window and their cascade order is not
  guaranteed, so each is one plain media block keyed on the ids these layers carry.
-->
<div
	id="player"
	{@attach gestures}
	data-shape={full ? 'full' : 'bar'}
	data-rise={rise || undefined}
	data-lyrics={lyrics.open ? 'on' : 'off'}
	data-dock={dock ?? 'none'}
	data-hold={holdState}
	class="static z-10 mx-2 mb-2 flex w-auto shrink-0 flex-col items-center gap-2 rounded-panel border border-haze bg-surface-100/85 px-3 py-2 backdrop-blur-xl sm:absolute sm:inset-x-0 sm:bottom-4 sm:mx-auto sm:mb-0 sm:min-h-[53px] sm:w-[min(100%-2rem,80rem)] sm:flex-row sm:justify-between sm:gap-0 sm:px-4 sm:py-1"
>
	<!-- micro only: the cover is the ground, and the top edge is the room -->
	{#if cover}
		<div id="player-cover" class="hidden" aria-hidden="true">
			<img src={cover} alt="" />
			<span></span>
		</div>
	{/if}
	<div id="room-rail" class="hidden" aria-hidden="true"></div>

	<div class="player-row flex w-full min-w-0 items-center gap-3 sm:contents">
		<TrackInfo />
		<div id="player-docks" class="ml-auto flex shrink-0 items-center gap-1 sm:order-4 sm:ml-0 sm:gap-2">
			<!-- chat is reachable on every route, in a room or not: outside one its
			     empty state is the feature's front door -->
			<!-- Shuffle only in the full shape: the bar has no room for it, and the queue
			     sheet — one tap away in either shape — carries the same button. -->
			<!-- Lyrics only in the full shape, like shuffle beside it: the bar has no room,
			     and reading words is what the full shape is for. Disabled rather than hidden
			     when there are none — a button that vanishes per track is worse than one
			     that greys out. -->
			{#if full}
				<button
					type="button"
					aria-label={lyrics.open ? 'Hide the lyrics' : 'Show the lyrics'}
					aria-pressed={lyrics.open}
					disabled={lyrics.status === 'none'}
					class="flex size-11 items-center justify-center rounded-art text-fog hover:text-chalk focus-visible:outline-2 focus-visible:outline-primary-200 disabled:opacity-40 disabled:hover:text-fog sm:size-7"
					class:bg-surface-200={lyrics.open}
					class:text-chalk={lyrics.open}
					onclick={() => (lyrics.open = !lyrics.open)}
				>
					<Icon src={MusicalNote} mini size="16" />
				</button>
				<button
					type="button"
					aria-label="Shuffle what is coming up"
					class="flex size-11 items-center justify-center rounded-art text-fog hover:text-chalk focus-visible:outline-2 focus-visible:outline-primary-200 sm:size-7"
					onclick={() => queue.shuffle()}
				>
					<Icon src={ArrowsRightLeft} mini size="16" />
				</button>
			{/if}
			<button
				type="button"
				id="dock-chat"
				aria-label="Open chat"
				aria-pressed={dock === 'chat'}
				class="relative flex size-11 items-center justify-center rounded-art text-fog hover:text-chalk focus-visible:outline-2 focus-visible:outline-primary-200 sm:size-7"
				class:bg-surface-200={dock === 'chat'}
				class:text-chalk={dock === 'chat'}
				onclick={() => toggle('chat')}
			>
				<Icon src={ChatBubbleOvalLeft} mini size="16" />
				{#if session.unread > 0}
					<span class="absolute right-0.5 top-0.5 min-w-4 rounded-full bg-primary-600 px-1 text-center font-mono text-[9px] font-medium leading-4 text-white">{session.unread}</span>
				{/if}
			</button>
			<button
				type="button"
				id="dock-queue"
				aria-label="Open queue"
				aria-pressed={dock === 'queue'}
				class="relative flex size-11 items-center justify-center rounded-art text-fog hover:text-chalk focus-visible:outline-2 focus-visible:outline-primary-200 sm:size-7"
				class:bg-surface-200={dock === 'queue'}
				class:text-chalk={dock === 'queue'}
				onclick={() => toggle('queue')}
			>
				<Icon src={QueueList} mini size="16" />
				{#key landed}
					{#if landed}<span class="queue-drop" aria-hidden="true"></span>{/if}
					<span
						class="queue-badge absolute right-0.5 top-0.5 min-w-4 rounded-full bg-primary-600 px-1 text-center font-mono text-[9px] font-medium leading-4 text-white"
						class:caught={landed !== null}>{queue.items.length}</span
					>
				{/key}
			</button>
			<!-- there from the first load, like lyrics beside it: greyed until there is a
			     record to open out, rather than a gap that fills in later -->
			<button
				type="button"
				id="player-shape"
				aria-label={full ? 'Close the full player' : 'Open the full player'}
				aria-expanded={full}
				disabled={!current.name}
				class="flex size-11 items-center justify-center rounded-art text-fog hover:text-chalk focus-visible:outline-2 focus-visible:outline-primary-200 disabled:opacity-40 disabled:hover:text-fog sm:size-7"
				onclick={() => (full ? collapse() : expand())}
			>
				<Icon src={full ? ChevronDown : ChevronUp} mini size="16" />
			</button>
			<Quality />
			<!-- ponytail: the slider is mouse-and-keyboard only by choice — on a phone
			     the hardware keys own volume and this costs 96px of a 350px row. -->
			<div class="hidden sm:block"><Volume /></div>
		</div>
	</div>

	<!-- Inside the same element as everything else. Nothing about the pane may remount
	     the player: the <audio> element and its graph live in this div. -->
	{#if full && lyrics.open}
		<Lyrics />
	{/if}

	<div class="player-row flex w-full min-w-0 items-center gap-3 sm:contents">
		<Controls />
		<SeekBar />
	</div>
	<!--
	  Two engines, one at a time, because they want opposite things. A room needs
	  the element: it starts on the first few seconds instead of the whole file,
	  and its rate is what the sync clock steers. On your own, nothing steers
	  anything and what matters is that two continuous tracks join without a hole,
	  which only a scheduled buffer can do. Swapping engines restarts the track at
	  the position the other one left — joining a room re-seeks everyone anyway.
	-->
	{#if session.inRoom}
		<Audio />
	{:else}
		<Gapless />
	{/if}
</div>



<style>
	/* The badge catches the drop. Same vocabulary as the session strip's hanging
	   droplets and the buffer gauge's rain — this app answers in water. */
	@keyframes queue-fall {
		0% {
			transform: translateY(-13px) scaleY(1.5);
			opacity: 0;
		}
		30% {
			opacity: 1;
		}
		100% {
			transform: translateY(0) scaleY(0.5);
			opacity: 0;
		}
	}

	@keyframes queue-catch {
		0%,
		100% {
			transform: scale(1);
		}
		45% {
			transform: scale(1.35);
		}
	}

	.queue-drop {
		position: absolute;
		right: 9px;
		top: 2px;
		width: 2px;
		height: 9px;
		border-radius: 999px;
		background: linear-gradient(to bottom, transparent, var(--color-primary-200));
		pointer-events: none;
		animation: queue-fall 420ms cubic-bezier(0.45, 0, 0.9, 0.45) forwards;
	}

	.queue-badge.caught {
		animation: queue-catch 380ms cubic-bezier(0.2, 0.7, 0.3, 1) 300ms;
	}

	/* the count still changes; it just does not move to say so */
	@media (prefers-reduced-motion: reduce) {
		.queue-drop {
			display: none;
		}
		.queue-badge.caught {
			animation: none;
		}
	}
</style>
