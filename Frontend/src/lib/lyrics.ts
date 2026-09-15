import type { LyricLine } from '$requests/lyrics';

/**
 * Index of the line being sung at `seconds`, or -1 before the first one.
 *
 * Binary search, because this runs on every animation frame: a linear scan of a
 * 120-line song is 120 comparisons sixty times a second for no reason.
 *
 * -1 is a real state, not a failure — the intro, before anybody sings — and it renders
 * as nothing highlighted rather than as line zero highlighted.
 */
export function activeIndexAt(lines: LyricLine[], seconds: number): number {
	let low = 0;
	let high = lines.length - 1;
	let found = -1;

	while (low <= high) {
		const middle = (low + high) >> 1;
		const at = lines[middle].at;

		// An untimed line among timed ones cannot be sung at any particular moment; treat it
		// as behind us so the search keeps moving rather than stalling on it.
		if (at === null || at <= seconds) {
			found = middle;
			low = middle + 1;
		} else {
			high = middle - 1;
		}
	}

	return found;
}
