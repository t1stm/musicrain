<script lang="ts" module>
	import type { IconSource } from 'svelte-hero-icons';

	export type SwipeAction = {
		icon: IconSource;
		/** The drop's fill once the pull is whole. */
		color: string;
		run: () => void;
		/** Said in the drop, with a tick, before `run` fires — so an action that moves or
		 *  removes the row is seen to land before the row goes. Without it, `run` is the
		 *  answer (a menu opening). */
		done?: string;
	};

	/** One action, or steps of it: the first a trigger's width out, the next two, and so on. */
	export type SwipeSteps = SwipeAction | SwipeAction[];
</script>

<script lang="ts">
	import type { Snippet } from 'svelte';
	import { Check, Icon } from 'svelte-hero-icons';
	import { haptic } from '$lib/haptics';
	import { stepAt, swipe } from '$lib/swipe';

	// A row a finger can slide, the way a mail app's full swipe works: carried past a
	// fifth of the row and let go, `right` or `left` fires. A side with steps goes on: each
	// the same width further than the last, the furthest reached firing — the drop's icon
	// and colour say which as it goes. Anything short of the first springs back — there is no half-open
	// row to tap, so the keys are pictures of the action, not buttons. A mouse never
	// swipes (see `swipe`), so on a desktop this is only the row.
	let {
		right,
		left,
		ignore,
		children
	}: { right: SwipeSteps; left: SwipeSteps; ignore?: string; children: Snippet } = $props();

	type Side = 'right' | 'left';

	const stepsOf = (side: Side) => [side === 'right' ? right : left].flat();

	// How far into the swipe the finger is, in triggers: 0 at rest, 1 where letting go
	// fires the first step, 2 the second. The key's colour is the first of that.
	let pull = $state(0);
	let toward = $state<Side>('right');
	/** What letting go now would fire. */
	let reached = $derived(stepsOf(toward)[stepAt(pull, stepsOf(toward).length)] ?? null);
	let row: HTMLElement;

	// The answer: the drop fills and stretches into a tick and the word, then the step
	// runs and the row goes home. 900ms, not the 600 a bare tick needed: the word has to
	// be read, and the stretch takes the first 260 of it.
	let done = $state<Side | null>(null);
	let fired = $state<SwipeAction | null>(null);
	let settle: ReturnType<typeof setTimeout>;

	function drag(dx: number) {
		// a new drag cuts the answer short but keeps the action; the (0, 0) that ends the
		// drag that fired it must not
		if (done && dx !== 0) finish();
		const was = reached;
		toward = dx > 0 ? 'right' : 'left';
		// a fifth of the row, but never under the 72px `swipe` needs to call it a swipe at
		// all — a full key that fires nothing on release would be a lie
		pull = Math.min(Math.abs(dx) / Math.max(row.offsetWidth / 5, 72), stepsOf(toward).length);
		// Every step reached, or given back, is felt as the finger crosses it — a key changing
		// colour is easy to miss under the thumb covering it. Not on the (0, 0) of letting go:
		// that is the step firing, not the finger crossing one.
		if (dx !== 0 && reached !== was) haptic();
	}

	function fire(side: Side) {
		const action = toward === side ? reached : null;
		if (!action) return;
		if (!action.done) return action.run();
		done = side;
		fired = action;
		settle = setTimeout(finish, 900);
	}

	function finish() {
		clearTimeout(settle);
		const action = fired;
		done = null;
		fired = null;
		action?.run();
	}
</script>

<div
	bind:this={row}
	class="swipe-row"
	data-done={done}
	style:--pull={Math.min(pull, 1)}
	{@attach swipe({
		// A row's menu (TrackMenu's <details>) drops over the rows below it, but in the DOM
		// it is still inside the row: without this every press on it drags the row.
		ignore: ignore ? `${ignore}, details` : 'details',
		// the steps tick for themselves, at their own widths
		haptic: false,
		drag,
		right: () => fire('right'),
		left: () => fire('left')
	})}
>
	<div class="swipe-track">
		{@render key('right')}
		{@render children()}
		{@render key('left')}
	</div>
</div>

<!-- `side` is the way the finger goes: a swipe right uncovers the key on the left edge.
     The key is the step that fired while it answers, else the one the pull has reached,
     else the first. -->
{#snippet key(side: Side)}
	{@const action = (done === side && fired) || (toward === side && reached) || stepsOf(side)[0]}
	<div data-side={side} style:--fill={action.color} aria-hidden="true">
		<div class="key {side === 'right' ? 'justify-end' : 'justify-start'}">
			<span class="face">
				<span class="drop">
					<Icon src={done === side ? Check : action.icon} mini size="18" />
					<span class="word">{action.done}</span>
				</span>
			</span>
		</div>
	</div>
{/snippet}

<style>
	.swipe-row {
		--reveal: 0px;
		/* x only: the desktop menu drops below the row and must not be cut off */
		overflow-x: clip;
		/* the list keeps its vertical scroll; the sideways drag is the row's */
		touch-action: pan-y;
	}
	/* wide enough for the drop once it has stretched into a tick and the word */
	.swipe-row[data-done='right'] {
		--reveal: 7.25rem;
	}
	.swipe-row[data-done='left'] {
		--reveal: -7.25rem;
	}

	.swipe-track {
		--offset: calc(var(--reveal) + var(--swipe-x, 0px));
		position: relative;
		left: clamp(-100%, var(--offset), 100%);
		transition: left 250ms cubic-bezier(0.2, 0.7, 0.3, 1);
	}

	/* Hung off the row's edges and carried in with it, so nothing sits under the row.
	   The key stretches to fill what the row uncovers, and the label stays against the
	   row — it travels with the finger instead of being left behind. */
	[data-side] {
		position: absolute;
		inset-block: 0;
		display: flex;
		padding: 0.25rem;
		transition: width 250ms cubic-bezier(0.2, 0.7, 0.3, 1);
	}
	[data-side='right'] {
		right: 100%;
		width: max(5.5rem, var(--offset));
	}
	[data-side='left'] {
		left: 100%;
		width: max(5.5rem, -1 * var(--offset));
	}

	.key {
		display: flex;
		width: 100%;
		align-items: center;
		font-size: 0.75rem;
		font-weight: 600;
		color: var(--color-chalk);
	}
	/* the drop's place: centred a key's width from the row, and wide enough to hold it
	   once it has stretched */
	.face {
		display: flex;
		min-width: 5rem;
		justify-content: center;
		padding-inline: 6px;
	}

	/* The same circle in every list, whatever the row's height: a key sized to the row
	   looked wrong in any row shorter than a search result's. Bare at first; it swells and
	   fills with the pull and is whole at the point where letting go fires it. */
	.drop {
		display: flex;
		width: 34px;
		height: 34px;
		align-items: center;
		justify-content: center;
		gap: 0;
		overflow: hidden;
		border-radius: 999px;
		box-shadow: inset 0 0 0 1.5px
			color-mix(in srgb, var(--fill) calc(40% + var(--pull) * 60%), var(--color-surface-400));
		background: color-mix(in srgb, var(--fill) calc(var(--pull) * 100%), transparent);
		scale: calc(0.6 + var(--pull) * 0.4);
		transition:
			background-color 150ms ease,
			scale 150ms cubic-bezier(0.2, 0.7, 0.3, 1),
			width 260ms cubic-bezier(0.2, 0.7, 0.3, 1),
			gap 260ms cubic-bezier(0.2, 0.7, 0.3, 1);
	}
	.word {
		max-width: 0;
		opacity: 0;
		overflow: hidden;
		white-space: nowrap;
		line-height: 1;
		transition:
			max-width 260ms cubic-bezier(0.2, 0.7, 0.3, 1),
			opacity 160ms ease 100ms;
	}

	/* under the finger nothing eases — the fill included, or it trails the pull */
	.swipe-row:global([data-swiping]) :is(.swipe-track, [data-side], .drop) {
		transition: none;
	}

	/* The answer: the drop fills, stretches outward into the tick and the word, and lands
	   with the app's ripple (its --animate-ripple, in the drop's colour). */
	.swipe-row[data-done='right'] [data-side='right'] .drop,
	.swipe-row[data-done='left'] [data-side='left'] .drop {
		width: 6rem;
		gap: 4px;
		background: var(--fill);
		scale: 1;
		animation: ripple 700ms ease-out;
	}
	.swipe-row[data-done] .word {
		max-width: 5rem;
		opacity: 1;
	}
	.swipe-row[data-done] .drop :global(svg) {
		width: 14px;
		height: 14px;
	}
	@keyframes ripple {
		from {
			box-shadow: 0 0 0 0 color-mix(in srgb, var(--fill) 70%, transparent);
		}
		to {
			box-shadow: 0 0 0 12px transparent;
		}
	}

	/* the fill, the tick and the word still say it; nothing moves to */
	@media (prefers-reduced-motion: reduce) {
		.swipe-track,
		[data-side],
		.drop,
		.word {
			transition: none;
		}
		.swipe-row[data-done] .drop {
			animation: none;
		}
	}
</style>
