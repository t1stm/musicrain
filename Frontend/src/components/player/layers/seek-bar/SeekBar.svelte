<script lang="ts">
	import audio from '$states/audio.svelte';
	import current from '$states/current.svelte';
	import session from '$states/session.svelte';

	import { getTimeString } from '$lib';
	import { SliderInteractions } from '$lib/sliderInteractions.svelte.js';
	import { seekTo } from '$lib/seek';

	const seekSeconds = 2;
	const slider = new SliderInteractions(seekSeconds);

	// dragging fires on every mousemove; in a room that would be sixty seeks a
	// second for everybody, so only the position you settle on is broadcast
	let seekTimer: ReturnType<typeof setTimeout> | undefined;
	slider.onChange = () => {
		const seconds = (slider.percentage / 100) * current.lengthSeconds;
		if (!session.inRoom) {
			seekTo(seconds);
			return;
		}
		clearTimeout(seekTimer);
		seekTimer = setTimeout(() => {
			seekTimer = undefined;
			seekTo(seconds);
		}, 150);
	};

	// The slider steps by percent from where the bar was last dragged. The arrow keys
	// step by seconds from where playback is, or, while a room seek is still waiting
	// to go out, from that seek, so a held key adds up instead of starting over.
	function keydown(event: KeyboardEvent) {
		if (!seekTimer) slider.percentage = currentPercentage;
		slider.stepValue = (seekSeconds / current.lengthSeconds) * 100;
		slider.keydown(event);
	}

	let buffered = $derived(current.lengthSeconds > 0 ? (audio.bufferedSeconds / current.lengthSeconds) * 100 : 0);
	let currentPercentage = $derived(current.lengthSeconds > 0 ? (audio.currentSeconds / current.lengthSeconds) * 100 : 0);
	// clamped like the `max-w-full` it replaces: past 100 a fill would slide off its left end
	const fillTransform = (percent: number) => `translateX(${Math.min(Math.max(percent, 0), 100) - 100}%)`;
	// Playback needs ~3s of runway; the gauge fills toward that, so a full column
	// is the moment the track resumes rather than an arbitrary level.
	const runwaySeconds = 3;
	let bufferedAhead = $derived(Math.max(audio.bufferedSeconds - audio.currentSeconds, 0));
	// In a room the loading happens while the track is held paused — that is the
	// whole point of the barrier — so `!audio.paused` on its own hides the gauge
	// for exactly the wait it exists to name. `awaitingLoad` is this client still
	// owing the room its `loaded`, which is the same wait wearing the other hat.
	let isBuffering = $derived(
		(!audio.paused || session.awaitingLoad) &&
			current.lengthSeconds > 0 &&
			bufferedAhead < runwaySeconds
	);
	let gaugeLevel = $derived(Math.min(bufferedAhead / runwaySeconds, 1) * 100);

	let currentTime = $derived(getTimeString(audio.currentSeconds));
	let maxTime = $derived(getTimeString(current.lengthSeconds));

  // The OS buttons step from where playback is. Through the slider they stepped
  // from wherever the bar was last dragged, and by percent rather than seconds.
  // `seekto` is the scrub on the notification's bar; without it the drag is dropped.
  $effect(() => {
    // in a separate effect to avoid re-running on every audio time update
    const by = (delta: number) =>
      seekTo(Math.min(Math.max(audio.currentSeconds + delta, 0), current.lengthSeconds));
    navigator.mediaSession.setActionHandler('seekbackward', (details) => by(-(details.seekOffset ?? 10)));
    navigator.mediaSession.setActionHandler('seekforward', (details) => by(details.seekOffset ?? 10));
    navigator.mediaSession.setActionHandler('seekto', (details) => {
      if (details.seekTime != null) seekTo(details.seekTime);
    });
  })

  // ponytail: whole seconds. This crosses into the browser process to redraw the
  // OS media notification, which shows seconds — so rounding is both the only
  // precision the call can use and the thing that stops the effect re-running on
  // every tick, since a $derived that lands on the same value propagates nothing.
  let positionSecond = $derived(Math.round(audio.currentSeconds));
  $effect(() => {
    navigator.mediaSession.setPositionState({
      duration: Math.max(current.lengthSeconds, positionSecond),
      position: positionSecond,
      playbackRate: 1,
    })
  })
</script>

<div id="seekbar" class="flex w-full min-w-0 max-w-lg items-center gap-2 sm:order-3 sm:flex-1">
	<div
		class="relative h-7 w-3.5 shrink-0 overflow-hidden rounded-art border bg-dark-0 transition-opacity duration-150"
		class:opacity-0={!isBuffering}
		class:border-primary-0={isBuffering}
		class:border-haze={!isBuffering}
		role="img"
		aria-label="Buffering"
		aria-hidden={!isBuffering}
	>
		{#if isBuffering}
			<div class="rain-streak"></div>
		{/if}
		<div
			class="absolute inset-x-0 bottom-0 border-t border-primary-500 bg-primary-0/40 duration-150"
			style:height={gaugeLevel + '%'}
		></div>
	</div>
	<span class="font-mono text-xs text-fog select-none">{currentTime}</span>
	<!-- The track grows while the blip shows — hover, focus and a finger alike. On `:hover`
	     alone a finger got the full-size blip on a track that stayed thin. -->
	<div
		class="relative flex h-2 w-full cursor-pointer touch-none rounded-lg bg-surface-200 duration-150 data-active:h-3 focus-visible:h-3 focus-visible:outline-4 outline-surface-300"
		data-active={slider.blipVisible || undefined}
		tabindex="0"
		role="slider"
		aria-valuenow={currentPercentage}
		aria-valuemin="0"
		aria-valuemax="100"
		onfocusin={slider.enter}
		onpointerenter={slider.enter}
		onpointerleave={slider.leave}
		onfocusout={slider.leave}
		onpointerup={slider.pointerUp}
		onpointercancel={slider.pointerUp}
		onpointerdown={slider.pointerDown}
		onpointermove={slider.pointerMove}
		onkeydown={keydown}
	>
		<!-- The fills are clipped to the track's own ends — rounded, or square in micro. Each
		     fill is the track's full width slid left until its end sits at the position, so
		     its rounded end keeps its shape from the first pixel instead of squashing as a
		     narrow width would. The blip stays outside: it overhangs. -->
		<div class="absolute inset-0 overflow-hidden rounded-[inherit]">
			<div
				class="absolute inset-0 bg-primary-0 rounded-lg duration-75"
				style:transform={fillTransform(buffered)}
			></div>
			<div
				class="absolute inset-0 rounded-lg duration-150"
				class:bg-primary-500={!isBuffering}
				class:bg-surface-400={isBuffering}
				style:transform={fillTransform(currentPercentage)}
			></div>
		</div>
		<div
			class="absolute left-0 -translate-x-1/2 rounded-full size-3 bg-white duration-75 transition-opacity"
			style:left={slider.hoverValue + '%'}
			style:opacity={slider.blipVisible ? 1 : 0}
		></div>
	</div>
	<span class="font-mono text-xs text-fog select-none">{maxTime}</span>
</div>
