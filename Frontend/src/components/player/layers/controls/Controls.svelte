<script lang="ts">
	import { Backward, Forward, Icon, Pause, Play } from 'svelte-hero-icons';
	import audio from '$states/audio.svelte';
	import queue from '$states/queue.svelte';
	import session from '$states/session.svelte';

	// ignored server-side before the first track has started, and there is no
	// error frame — so nothing to handle, it simply does not move
	function playPause() {
		if (session.inRoom) session.send('playpause');
		else audio.paused = !audio.paused;
	}

	const buttons = $derived([
		{
			icon: Backward,
			onClick: () => queue.previousTrack()
		},
		{
			icon: !audio.paused ? Pause : Play,
			onClick: playPause
		},
		{
			icon: Forward,
			onClick: () => queue.nextTrack(),
			// the gap between reaching for skip and pressing it is enough to start
			// the next encode in
			onHover: () => queue.preloadNext()
		}
	])

  // The lock screen asks for a state, not a flip: a `play` that lands while this
  // already plays must not pause it. The room only knows the flip, so it is sent
  // only when it would change something.
  function setPaused(paused: boolean) {
    if (paused !== audio.paused) playPause();
  }

  $effect(() => {
    // static navigator fields are not very reactive. shouldn't update them every time
    navigator.mediaSession.setActionHandler('play', () => setPaused(false));
    navigator.mediaSession.setActionHandler('pause', () => setPaused(true));
    navigator.mediaSession.setActionHandler('previoustrack', () => {
      queue.previousTrack();
    });
    navigator.mediaSession.setActionHandler('nexttrack', () => {
      queue.nextTrack();
    });
  })

  $effect(() => {
    navigator.mediaSession.playbackState = audio.paused ? 'paused' : 'playing'
  })
</script>

<div id="controls" class="flex shrink-0 items-center gap-1 sm:order-1 sm:gap-2">
	{#each buttons as button (button.icon)}
		<button
			class="flex size-11 cursor-pointer items-center justify-center rounded-lg outline-surface-300 ring-0 duration-75 focus-visible:bg-surface-300 focus-visible:outline-5 sm:size-8"
			onclick={button.onClick}
			onmouseenter={button.onHover}
			onfocus={button.onHover}
		>
			<Icon src={button.icon} color="white" mini size="24" />
		</button>
	{/each}
</div>
