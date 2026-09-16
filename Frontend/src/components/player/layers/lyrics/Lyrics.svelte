<script lang="ts">
	import audio from '$states/audio.svelte';
	import current from '$states/current.svelte';
	import lyrics from '$states/lyrics.svelte';
	import { activeIndexAt } from '$lib/lyrics';
	import { seekTo } from '$lib/seek';

	// The pane owns the fetch: it mounts when the listener opens it and when the track
	// changes under it, which are exactly the two moments the words should be asked for.
	$effect(() => {
		// read so the effect re-runs on a track change
		current.id;
		lyrics.load();
	});

	let synced = $derived(lyrics.status === 'ready' && lyrics.lyrics?.type === 'Synchronized');

	let visible = $state(true);
	$effect(() => {
		const onVisibility = () => (visible = document.visibilityState === 'visible');
		onVisibility();
		document.addEventListener('visibilitychange', onVisibility);
		return () => document.removeEventListener('visibilitychange', onVisibility);
	});

	// The ticker. Started only while there is something to follow and somebody to see
	// it; stopped the moment any of that stops holding, so a paused player in a
	// background tab costs nothing.
	$effect(() => {
		const lines = lyrics.lyrics?.lines;
		if (!synced || !lines || audio.paused || !visible) return;

		let frame = 0;
		const tick = () => {
			// positionNow, not currentSeconds: the latter is a 10 Hz sample for the UI, and at
			// 100 ms granularity a line lands visibly late against the voice. This one is the
			// interpolated position the room's clock already trusts, with the device's output
			// latency subtracted — exactly the correction lyrics need.
			const index = activeIndexAt(lines, audio.positionNow());
			// Written only when it changed, so an effect runs per line rather than per frame.
			if (index !== lyrics.activeIndex) lyrics.activeIndex = index;
			frame = requestAnimationFrame(tick);
		};

		frame = requestAnimationFrame(tick);
		return () => cancelAnimationFrame(frame);
	});

	let pane = $state<HTMLDivElement | null>(null);
	$effect(() => {
		const index = lyrics.activeIndex;
		if (!synced || index < 0 || !pane) return;

		const line = pane.querySelector<HTMLElement>(`[data-index="${index}"]`);
		line?.scrollIntoView({
			block: 'center',
			behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth'
		});
	});
</script>

<!--
  Business logic only: the right words in the right order with the right one marked.
  data-active and data-sung are the hooks the styling hangs off, and app.css already
  shrinks --cover for #player[data-shape='full'][data-lyrics='on'].
-->
<div id="player-lyrics" bind:this={pane} data-type={lyrics.lyrics?.type ?? 'none'}>
	{#if lyrics.status === 'loading'}
		<p data-state="loading">Looking for the words…</p>
	{:else if lyrics.status === 'none'}
		<p data-state="none">No lyrics for this track.</p>
	{:else if lyrics.status === 'error'}
		<!-- "nothing found" and "the service is down" are different facts about the world. -->
		<p data-state="error">The lyrics service is not answering.</p>
	{:else if lyrics.lyrics}
		{#if synced}
			<ol>
				{#each lyrics.lyrics.lines as line, index (index)}
					<li
						data-index={index}
						data-active={index === lyrics.activeIndex}
						data-sung={index < lyrics.activeIndex}
						data-blank={line.text.trim() === ''}
					>
						<button type="button" onclick={() => line.at !== null && seekTo(line.at)}>
							{line.text}
						</button>
					</li>
				{/each}
			</ol>
		{:else}
			<!-- No ticker, no scrolling and no click targets: there is no clock to click to. -->
			<p data-state="plain">{lyrics.lyrics.text}</p>
		{/if}
	{/if}
</div>
