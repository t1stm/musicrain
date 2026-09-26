import type { Attachment } from 'svelte/attachments';
import { haptic } from './haptics';

/**
 * Where a dragged row lands, as a position in the list: past the middle of a row is past the
 * row. `mids` are the rows' vertical centres in list order.
 */
export function landing(from: number, y: number, mids: number[]): number {
	const above = mids.findIndex((mid, at) => at < from && y < mid);
	if (above !== -1) return above;
	const below = mids.findLastIndex((mid, at) => at > from && y > mid);
	return below !== -1 ? below : from;
}

/**
 * Drag to reorder with any pointer. HTML drag and drop never starts from a touch on most
 * phones, so the queue could not be reordered on one at all.
 *
 * Rows carry `data-index`, their index in whatever `move` is given. A finger starts on the
 * row's `data-grip` only, which leaves the rest of the row free to scroll the list; a mouse
 * can take the row anywhere. The row being carried gets `data-dragging` and the one it will
 * land against `data-drop="before" | "after"` — app.css draws both.
 *
 * ponytail: no auto-scroll at the list's edges. A long move on a phone is two drags, or
 * "Play next" from the track's menu. Add a rAF loop here if that gets in the way.
 */
export const reorder =
	(move: (from: number, to: number) => void): Attachment<HTMLElement> =>
	(list) => {
		// a drag lets go over a row, and that row must not also take it as a click
		let dragged = false;
		const swallow = (event: MouseEvent) => {
			if (!dragged) return;
			dragged = false;
			event.preventDefault();
			event.stopPropagation();
		};

		const start = (down: PointerEvent) => {
			dragged = false;
			const target = down.target as Element;
			const row = target.closest<HTMLElement>('[data-index]');
			if (!row || !down.isPrimary || down.button !== 0 || target.closest('a, button, input')) return;
			if (down.pointerType !== 'mouse' && !target.closest('[data-grip]')) return;

			const rows = [...list.querySelectorAll<HTMLElement>('[data-index]')];
			const from = rows.indexOf(row);
			let to = from;
			let marked: HTMLElement | undefined;

			const follow = (event: PointerEvent) => {
				if (event.pointerId !== down.pointerId) return;
				const dy = event.clientY - down.clientY;
				if (!dragged && Math.abs(dy) < 6) return;
				// felt as it comes up, and at every place it is carried to below
				if (!dragged) haptic();
				dragged = true;
				row.dataset.dragging = '';
				row.style.translate = `0 ${dy}px`;

				// measured live, so a list that scrolls under the drag still lands true
				const mids = rows.map((each) => {
					const box = each.getBoundingClientRect();
					return box.top + box.height / 2;
				});
				const was = to;
				to = landing(from, event.clientY, mids);
				if (to !== was) haptic();
				if (marked) delete marked.dataset.drop;
				marked = to === from ? undefined : rows[to];
				if (marked) marked.dataset.drop = to > from ? 'after' : 'before';
			};

			const end = (event: PointerEvent) => {
				if (event.pointerId !== down.pointerId) return;
				removeEventListener('pointermove', follow);
				removeEventListener('pointerup', end);
				removeEventListener('pointercancel', end);
				delete row.dataset.dragging;
				row.style.translate = '';
				if (marked) delete marked.dataset.drop;
				if (event.type === 'pointerup' && dragged && to !== from) {
					move(Number(row.dataset.index), Number(rows[to].dataset.index));
				}
			};

			addEventListener('pointermove', follow);
			addEventListener('pointerup', end);
			addEventListener('pointercancel', end);
		};

		list.addEventListener('pointerdown', start);
		list.addEventListener('click', swallow, true);
		return () => {
			list.removeEventListener('pointerdown', start);
			list.removeEventListener('click', swallow, true);
		};
	};
