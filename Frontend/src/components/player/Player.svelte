<script lang="ts">
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

	type Dock = 'queue' | 'chat' | null;
	let { dock = $bindable<Dock>(null) }: { dock?: Dock } = $props();

	// The full player: the same element, a different shape. A separate component would
	// mount a second <audio> or unmount this one, and both stop the music.
	let full = $state(false);
	closeOnBack(
		() => full,
		() => (full = false)
	);

	function toggle(tab: Exclude<Dock, null>) {
		dock = dock === tab ? null : tab;
		// A wide screen holds both — the full player makes room for the sheet beside it.
		// A phone cannot: the sheet is 70dvh of it, so opening one leaves the other.
		if (dock && !window.matchMedia('(min-width: 640px)').matches) full = false;
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
  Three shapes, one set of children. Compact stacks two rows and docks in flow at
  the foot of the column — which is why no page pads for a player any more. The
  two row wrappers dissolve at `sm` (`display: contents`) and `order` deals the
  same children into the single floating bar. Micro is driven from app.css, not
  from here: `micro:` and `sm:` both match a short wide window and their cascade
  order is not guaranteed, so the small mode is one plain media block keyed on
  the ids these layers already carry.
-->
<div
	id="player"
	data-shape={full ? 'full' : 'bar'}
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
			{#if current.name}
				<button
					type="button"
					id="player-shape"
					aria-label={full ? 'Close the full player' : 'Open the full player'}
					aria-expanded={full}
					class="flex size-11 items-center justify-center rounded-art text-fog hover:text-chalk focus-visible:outline-2 focus-visible:outline-primary-200 sm:size-7"
					onclick={() => (full = !full)}
				>
					<Icon src={full ? ChevronDown : ChevronUp} mini size="16" />
				</button>
			{/if}
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
