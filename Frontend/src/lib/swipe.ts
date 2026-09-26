import { tick } from 'svelte';
import type { Attachment } from 'svelte/attachments';

export type Direction = 'up' | 'down' | 'left' | 'right';

/** Movement under this is a press that wobbled, not a gesture. */
const SLOP = 10;

/**
 * What a finished drag meant: far enough, or short but fast enough to be a flick. Anything
 * short of both springs back. The drag is locked to one axis before it gets here, so one of
 * the two deltas is always zero.
 *
 * ponytail: average speed over the whole drag, not the speed at release. A slow drag that
 * ends in a flick reads as slow; sample the last few moves if that turns out to matter.
 */
export function settle(dx: number, dy: number, ms: number): Direction | null {
	const distance = Math.max(Math.abs(dx), Math.abs(dy));
	const flick = distance > 24 && distance / Math.max(ms, 1) > 0.5;
	if (distance < 72 && !flick) return null;
	if (Math.abs(dx) > Math.abs(dy)) return dx < 0 ? 'left' : 'right';
	return dy < 0 ? 'up' : 'down';
}

/**
 * Which of `count` steps a pull has reached, the pull measured in triggers: every step is
 * one trigger further on than the last. -1 short of the first; past the last is the last.
 */
export function stepAt(pull: number, count: number): number {
	return Math.min(Math.floor(pull), count) - 1;
}

/** Whether a press on `target` is left alone: inside `ignore`, unless a `handle` is nearer. */
export function ignored(target: Element, ignore?: string, handle?: string): boolean {
	if (!ignore) return false;
	const hit = target.closest(handle ? `${ignore}, ${handle}` : ignore);
	return !!hit && !(handle && hit.matches(handle));
}

/**
 * Sees `node` out the way a swipe toward `direction` sends it: CSS carries it wherever
 * `data-swiped` points, and `then` runs once the node's own transitions have got it there
 * — no transition, no wait. The attribute goes once `then` is done, a promise it hands
 * back included, and its change is on the page.
 *
 * Transitions only: an animation can loop, and its `finished` never comes.
 */
async function land(node: HTMLElement, direction: Direction, then: () => unknown) {
	node.dataset.swiped = direction;
	const going = node.getAnimations().filter((a) => a instanceof CSSTransition);
	await Promise.allSettled(going.map((a) => a.finished));
	await then();
	await tick();
	delete node.dataset.swiped;
}

/** A direction's handler. One that hands back a promise keeps the offset until it settles. */
type Swipe = Partial<Record<Direction, () => unknown>> & {
	/** A press that went nowhere. Handed the event, so the caller can ask what was under it. */
	tap?: (event: PointerEvent) => void;
	/** Presses that start on these are the control's own — a slider, a scrolling pane. */
	ignore?: string;
	/** A way back in through `ignore`: a press on one of these is the surface's again.
	 *  Whichever of the two is nearer the press decides. */
	handle?: string;
	/** The live offset, for what CSS cannot work out from it alone. (0, 0) once it lets go,
	 *  after the direction's handler has run. */
	drag?: (dx: number, dy: number) => void;
};

/**
 * Touch gestures on one surface. While a drag is live the node carries `data-swiping` and
 * the offset as `--swipe-x` / `--swipe-y`, so CSS decides what follows the finger and how
 * far. A drag that lands carries `data-swiped="<direction>"` until its handler has run, so
 * CSS decides where it finishes too. The node needs `touch-action: none` too, or the
 * browser takes the drag for a scroll and cancels it.
 *
 * A mouse never swipes: dragging across text is a selection. It still taps.
 */
export const swipe =
	(handlers: Swipe): Attachment<HTMLElement> =>
	(node) => {
		// A drag that began on a link or a tab must not also click it once it lets go.
		let dragged = false;
		const swallow = (event: MouseEvent) => {
			if (!dragged) return;
			dragged = false;
			event.preventDefault();
			event.stopPropagation();
		};

		const start = (down: PointerEvent) => {
			dragged = false;
			if (!down.isPrimary || down.button !== 0) return;
			if (ignored(down.target as Element, handlers.ignore, handlers.handle)) return;

			const began = performance.now();
			const mouse = down.pointerType === 'mouse';
			let axis: 'x' | 'y' | null = null;
			let moved = false;
			let dx = 0;
			let dy = 0;

			const move = (event: PointerEvent) => {
				if (event.pointerId !== down.pointerId) return;
				const x = event.clientX - down.clientX;
				const y = event.clientY - down.clientY;
				if (!axis) {
					if (Math.hypot(x, y) < SLOP) return;
					moved = true;
					if (mouse) return;
					axis = Math.abs(x) > Math.abs(y) ? 'x' : 'y';
					dragged = true;
					node.dataset.swiping = axis;
				}
				dx = axis === 'x' ? x : 0;
				dy = axis === 'y' ? y : 0;
				node.style.setProperty('--swipe-x', `${dx}px`);
				node.style.setProperty('--swipe-y', `${dy}px`);
				handlers.drag?.(dx, dy);
			};

			const end = async (event: PointerEvent) => {
				if (event.pointerId !== down.pointerId) return;
				removeEventListener('pointermove', move);
				removeEventListener('pointerup', end);
				removeEventListener('pointercancel', end);
				// A flick lets go in the same frame as its last move, before that move is drawn.
				// Draw it now, while transitions are still off — otherwise letting go eases the
				// node across that last step first, and a landed swipe waits for it.
				getComputedStyle(node).getPropertyValue('translate');
				delete node.dataset.swiping;

				const cancelled = event.type === 'pointercancel';
				const direction = !cancelled && axis ? settle(dx, dy, performance.now() - began) : null;
				const handler = direction && handlers[direction];
				// Seen out, not dropped: the node keeps the finger's offset and `land` carries it
				// on from there. The offset holds until the handler's change is on the page, or
				// the node heads home for a frame first.
				if (handler) await land(node, direction, handler);
				else if (!cancelled && !moved) handlers.tap?.(event);
				node.style.removeProperty('--swipe-x');
				node.style.removeProperty('--swipe-y');
				handlers.drag?.(0, 0);
			};

			addEventListener('pointermove', move);
			addEventListener('pointerup', end);
			addEventListener('pointercancel', end);
		};

		node.addEventListener('pointerdown', start);
		node.addEventListener('click', swallow, true);
		return () => {
			node.removeEventListener('pointerdown', start);
			node.removeEventListener('click', swallow, true);
		};
	};
