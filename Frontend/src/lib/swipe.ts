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

type Swipe = Partial<Record<Direction, () => void>> & {
	/** A press that went nowhere. Handed the event, so the caller can ask what was under it. */
	tap?: (event: PointerEvent) => void;
	/** Presses that start on these are the control's own — a slider, a scrolling pane. */
	ignore?: string;
};

/**
 * Touch gestures on one surface. While a drag is live the node carries `data-swiping` and
 * the offset as `--swipe-x` / `--swipe-y`, so CSS decides what follows the finger and how
 * far. The node needs `touch-action: none` too, or the browser takes the drag for a scroll
 * and cancels it.
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
			if (handlers.ignore && (down.target as Element).closest(handlers.ignore)) return;

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
			};

			const end = (event: PointerEvent) => {
				if (event.pointerId !== down.pointerId) return;
				removeEventListener('pointermove', move);
				removeEventListener('pointerup', end);
				removeEventListener('pointercancel', end);
				node.style.removeProperty('--swipe-x');
				node.style.removeProperty('--swipe-y');
				delete node.dataset.swiping;

				if (event.type === 'pointercancel') return;
				if (!moved) return handlers.tap?.(event);
				const direction = axis && settle(dx, dy, performance.now() - began);
				if (direction) handlers[direction]?.();
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
