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
 * How fast a list scrolls under a carried row, in px/ms: not at all in the middle, faster the
 * deeper the pointer is into the `edge` px at the list's top or bottom, flat out past them.
 * The edge shrinks on a short list, so it always has a middle to hold still in.
 */
export function edgeSpeed(y: number, top: number, bottom: number, edge = 56, max = 0.8): number {
	const zone = Math.min(edge, (bottom - top) / 4);
	const depth = y < top + zone ? y - top - zone : y > bottom - zone ? y - bottom + zone : 0;
	return Math.max(-1, Math.min(1, depth / zone)) * max;
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
 * Held near the top or bottom of whatever scrolls the list, the row scrolls it — the list
 * itself for the queue, the page for a playlist. A finger holding still sends no events, so
 * that runs on its own frames rather than on the pointer's.
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
			let y = down.clientY;

			// The limit is taken now: the carried row's own translate adds to what the list can
			// scroll, and a row chasing that grows it without end.
			let scroller: HTMLElement | null = list;
			while (scroller && !/auto|scroll/.test(getComputedStyle(scroller).overflowY)) scroller = scroller.parentElement;
			const scrolledFrom = scroller?.scrollTop ?? 0;
			const limit = scroller ? scroller.scrollHeight - scroller.clientHeight : 0;
			let frame = 0;
			let then = 0;

			const place = () => {
				// the row moves with the list it is in, so what the list scrolled is put back
				row.style.translate = `0 ${y - down.clientY + (scroller?.scrollTop ?? 0) - scrolledFrom}px`;

				// measured live, so a list that scrolls under the drag still lands true
				const mids = rows.map((each) => {
					const box = each.getBoundingClientRect();
					return box.top + box.height / 2;
				});
				const was = to;
				to = landing(from, y, mids);
				if (to !== was) haptic();
				if (marked) delete marked.dataset.drop;
				marked = to === from ? undefined : rows[to];
				if (marked) marked.dataset.drop = to > from ? 'after' : 'before';
			};

			const scroll = (now: number) => {
				frame = 0;
				if (!scroller) return;
				const box = scroller.getBoundingClientRect();
				const step = edgeSpeed(y, box.top, box.bottom) * (then ? now - then : 16);
				const was = scroller.scrollTop;
				// whole pixels, and at least one: a fractional step can round away to nothing
				scroller.scrollTop = Math.max(0, Math.min(limit, was + (Math.round(step) || Math.sign(step))));
				// stopped by the middle or the end; the next move looks again
				then = scroller.scrollTop === was ? 0 : now;
				if (!then) return;
				place();
				frame = requestAnimationFrame(scroll);
			};

			const follow = (event: PointerEvent) => {
				if (event.pointerId !== down.pointerId) return;
				y = event.clientY;
				if (!dragged && Math.abs(y - down.clientY) < 6) return;
				// felt as it comes up, and at every place it is carried to below
				if (!dragged) haptic();
				dragged = true;
				row.dataset.dragging = '';
				place();
				frame ||= requestAnimationFrame(scroll);
			};

			const end = (event: PointerEvent) => {
				if (event.pointerId !== down.pointerId) return;
				cancelAnimationFrame(frame);
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
