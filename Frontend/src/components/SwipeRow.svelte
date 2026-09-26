<script lang="ts" module>
	import type { IconSource } from 'svelte-hero-icons';

	export type SwipeAction = {
		label: string;
		icon: IconSource;
		/** The key's fill once the pull is whole. */
		color: string;
		run: () => void;
		/** Said on the key, with a tick, before `run` fires — so an action that moves or
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
	import { stepAt, swipe } from '$lib/swipe';

	// A row a finger can slide, the way a mail app's full swipe works: carried past a
	// fifth of the row and let go, `right` or `left` fires. A side with steps goes on: each
	// the same width further than the last, the furthest reached firing — the key says
	// which as it goes. Anything short of the first springs back — there is no half-open
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

	// The answer: the key holds, filled, with a tick, then the step runs and the row
	// goes home.
	let done = $state<Side | null>(null);
	let fired = $state<SwipeAction | null>(null);
	let settle: ReturnType<typeof setTimeout>;

	function drag(dx: number) {
		// a new drag cuts the answer short but keeps the action; the (0, 0) that ends the
		// drag that fired it must not
		if (done && dx !== 0) finish();
		toward = dx > 0 ? 'right' : 'left';
		// a fifth of the row, but never under the 72px `swipe` needs to call it a swipe at
		// all — a full key that fires nothing on release would be a lie
		pull = Math.min(Math.abs(dx) / Math.max(row.offsetWidth / 5, 72), stepsOf(toward).length);
	}

	function fire(side: Side) {
		const action = toward === side ? reached : null;
		if (!action) return;
		if (!action.done) return action.run();
		done = side;
		fired = action;
		settle = setTimeout(finish, 600);
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
			<span class="flex w-20 flex-col items-center gap-1">
				<Icon src={done === side ? Check : action.icon} mini size="18" />
				{done === side ? action.done : action.label}
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
	.swipe-row[data-done='right'] {
		--reveal: 5.5rem;
	}
	.swipe-row[data-done='left'] {
		--reveal: -5.5rem;
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

	/* Bare at first; the fill comes in with the pull and is whole at the point where
	   letting go fires it. */
	.key {
		display: flex;
		width: 100%;
		align-items: center;
		border-radius: var(--radius-row);
		background: color-mix(in srgb, var(--fill) calc(var(--pull) * 100%), transparent);
		font-size: 0.75rem;
		font-weight: 600;
		color: var(--color-chalk);
		transition: background-color 150ms ease;
	}

	/* under the finger nothing eases — the fill included, or it trails the pull */
	.swipe-row:global([data-swiping]) :is(.swipe-track, [data-side], .key) {
		transition: none;
	}

	.swipe-row[data-done='right'] [data-side='right'] .key,
	.swipe-row[data-done='left'] [data-side='left'] .key {
		background: var(--fill);
	}
	.swipe-row[data-done='right'] [data-side='right'] span,
	.swipe-row[data-done='left'] [data-side='left'] span {
		animation: done 420ms cubic-bezier(0.2, 0.7, 0.3, 1);
	}
	@keyframes done {
		0% {
			scale: 0.5;
			opacity: 0;
		}
		60% {
			scale: 1.12;
			opacity: 1;
		}
		100% {
			scale: 1;
		}
	}

	/* the fill and the tick still say it; nothing moves to */
	@media (prefers-reduced-motion: reduce) {
		.swipe-track,
		[data-side] {
			transition: none;
		}
		.swipe-row[data-done] span {
			animation: none;
		}
	}
</style>
