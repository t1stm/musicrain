import type { Attachment } from 'svelte/attachments';
import { scrollerOf } from './reorder';

/**
 * How many rows a long list draws at first, and how many more each time the reader nears its
 * end. A row costs about 8 ms on a slow phone, so a whole 1600-track playlist at once is a
 * thirteen-second freeze; fifty is a pause of a few tenths, spent while they are still off screen.
 */
export const STEP = 50;

/**
 * One key per row that survives edits around it: the id, and which copy of that id it is. A list
 * can hold a track twice, so the id alone is not unique — but keyed by its position every row
 * after a removed one is a new row, and Svelte builds all of them again.
 */
export function keysOf(items: { id: string }[]): string[] {
	const seen = new Map<string, number>();
	return items.map(({ id }) => {
		const copy = seen.get(id) ?? 0;
		seen.set(id, copy + 1);
		return `${id}#${copy}`;
	});
}

/**
 * Calls `more` while `node` is within `ahead` px of the end of whatever scrolls it. The root is
 * that scroller, not the viewport: `rootMargin` only widens the root, so with the viewport as the
 * root a sentinel inside `.page` would be seen only once it was already on the screen.
 */
export const nearEnd =
	(more: () => void, ahead = 1200): Attachment<HTMLElement> =>
	(node) => {
		const observer = new IntersectionObserver(
			(entries) => {
				if (!entries.some((entry) => entry.isIntersecting)) return;
				more();
				// Still in reach once the new rows are in? Observing again reports again, and
				// it is measured after they have rendered, not before.
				observer.unobserve(node);
				observer.observe(node);
			},
			{ root: scrollerOf(node), rootMargin: `0px 0px ${ahead}px 0px` }
		);
		observer.observe(node);
		return () => observer.disconnect();
	};
