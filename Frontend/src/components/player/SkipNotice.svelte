<script lang="ts">
	import { sourceOf } from '$lib';
	import { closeOnBack } from '$lib/backWatcher.svelte';
	import audio from '$states/audio.svelte';
	import session from '$states/session.svelte';
	import skipped from '$states/skipped.svelte';

	// Mounted once, from the layout, which is the whole of "only one notice": a
	// failure while it is open is one more row in it, never a second dialog.
	let notice = $state<HTMLDialogElement>();
	let count = $derived(skipped.tracks.length);
	let mode = $derived(session.inRoom ? 'room' : skipped.stopped ? 'stopped' : 'alone');

	// the gate's rain: same columns, same irregular hang
	const drops = Array.from({ length: 26 }, (_, index) => index);
	// One dry column per skipped track. 7 shares no factor with 26, so the first 26
	// failures each land somewhere new, scattered rather than in a row.
	let dry = $derived(
		new Set(skipped.tracks.slice(0, drops.length).map((_, index) => (index * 7 + 3) % drops.length))
	);

	$effect(() => {
		if (count && notice && !notice.open) notice.showModal();
	});

	// Back, the back gesture and Escape take this before whatever is open under it —
	// without it Escape would close this natively and the full player behind it too.
	closeOnBack(
		() => count > 0,
		() => notice?.close()
	);

	const spoken = (reason: string) =>
		/^\d+$/.test(reason)
			? `The server answered ${reason}.`
			: reason === 'decode'
				? 'It would not decode.'
				: reason === 'unavailable'
					? 'The player could not open it.'
					: 'The download did not finish.';

	function tryAgain() {
		// Play on a track that gave up is a retry; see the paused effect in Gapless.
		audio.paused = false;
		notice?.close();
	}

	function imageFallback(event: Event) {
		const image = event.currentTarget as HTMLImageElement;
		if (!image.src.endsWith('/empty.png')) image.src = '/empty.png';
	}
</script>

<!-- The gate's panel with the story turned over: there the rain hangs waiting for
     a press, here some of it never arrived. Each skipped track takes one drop out
     of the strip and leaves the line under it dry. -->
<dialog
	bind:this={notice}
	class="gate"
	closedby="any"
	aria-labelledby="skip-title"
	aria-describedby="skip-body"
	onclose={() => {
		// a failure that landed between close() and this event has already opened it again
		if (!notice?.open) skipped.dismiss();
	}}
>
	<div class="gate-rain" aria-hidden="true">
		{#each drops as index (index)}
			<span
				class="gate-drop"
				data-dry={dry.has(index) || undefined}
				style:--i={index}
				style:--hang={18 + ((index * 11) % 26)}
				style:left="{(index / (drops.length - 1)) * 100}%"
			></span>
			{#if dry.has(index)}
				<span class="dry" style:left="{(index / (drops.length - 1)) * 100}%"></span>
			{/if}
		{/each}
	</div>
	<div class="p-5 sm:p-6">
		<p class="eyebrow mb-3 text-ember">
			{mode === 'room' ? 'missed' : mode === 'stopped' ? 'stopped' : 'skipped'}
		</p>
		<h2 id="skip-title" class="font-display mb-3 text-xl font-extralight text-chalk">
			{#if mode === 'stopped'}
				Stopped after three tracks didn't load
			{:else if mode === 'room'}
				{count === 1 ? 'A track' : `${count} tracks`} didn't load for you
			{:else}
				Skipped {count === 1 ? 'a track' : `${count} tracks`} that didn't load
			{/if}
		</h2>
		<p id="skip-body" class="mb-4 text-sm leading-relaxed text-fog">
			{#if mode === 'stopped'}
				Three in a row points to the server, not the tracks, so playback is paused.
			{:else if mode === 'room'}
				The room played on without you. You'll hear the next track as usual.
			{:else}
				Playback moved on. Skipped tracks stay in your queue, so you can try them again.
			{/if}
		</p>

		<!-- a search row without its controls: nothing here navigates, since a link
		     would open its page underneath a modal. On a phone on its side the whole
		     panel already scrolls, and a scroller inside a scroller fights the thumb. -->
		<ul
			class="-mx-1 mb-6 max-h-[min(18rem,40dvh)] divide-y divide-haze overflow-y-auto overscroll-contain px-1 [@media(max-height:480px)]:max-h-none"
		>
			{#each skipped.tracks as { track, reason } (track.id)}
				<li class="grid grid-cols-[2.75rem_minmax(0,1fr)_auto] items-center gap-3 py-2">
					<img
						src={track.thumbnailUrl ?? '/empty.png'}
						alt=""
						class="size-11 rounded-art object-cover"
						onerror={imageFallback}
					/>
					<div class="min-w-0">
						<p class="line-clamp-2 text-sm font-medium leading-snug text-chalk">{track.name}</p>
						<p class="truncate text-[0.79rem] text-fog">
							{track.artist}{#if track.album}&nbsp;·&nbsp;{track.album}{/if}&nbsp;·&nbsp;<span
								class="font-mono">{sourceOf(track.id).name}</span
							>
						</p>
					</div>
					<!-- outlined, like "long" on a search row: a remark about the row, not its identity -->
					<span
						class="rounded-full border border-ember/45 px-1.5 py-px font-mono text-[0.6rem] uppercase tracking-[0.09em] text-ember"
						aria-hidden="true">{reason}</span
					>
					<span class="sr-only">{spoken(reason)}</span>
				</li>
			{/each}
		</ul>

		<!-- the list scrolls, and a scroller is focusable too: the button is where a
		     modal should land -->
		<!-- svelte-ignore a11y_autofocus -->
		<button
			type="button"
			autofocus
			onclick={mode === 'stopped' ? tryAgain : () => notice?.close()}
			class="min-h-11 w-full rounded-row bg-primary-600 px-4 py-2 text-sm font-semibold text-white hover:bg-primary-0 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-200 sm:w-auto"
		>
			{mode === 'stopped' ? 'Try again' : 'Keep listening'}
		</button>
	</div>
</dialog>

<style>
	/* The drop over a dry column falls and stops short of the line. It runs once,
	   when the column goes dry — on opening for the first, on arrival for the rest. */
	.gate-drop[data-dry] {
		animation: fall-short 0.5s cubic-bezier(0.45, 0, 0.9, 0.45) forwards;
	}

	@keyframes fall-short {
		to {
			transform: translateY(-4px);
			opacity: 0;
		}
	}

	/* a break in the hairline, painted over it in the panel's own colour */
	.dry {
		position: absolute;
		bottom: -1px;
		width: 9px;
		height: 1px;
		translate: -50% 0;
		background: var(--color-surface-100);
	}

	li {
		animation: row-in 0.3s cubic-bezier(0.2, 0.7, 0.3, 1);
	}

	@keyframes row-in {
		from {
			opacity: 0;
			translate: 0 6px;
		}
	}

	/* keep the meaning, drop the movement: the drop is simply gone */
	@media (prefers-reduced-motion: reduce) {
		.gate-drop[data-dry] {
			animation: none;
			opacity: 0;
		}
		li {
			animation: none;
		}
	}
</style>
