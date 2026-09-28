import type { SearchResult } from '$states/search.svelte';
import { AudioApiError } from '$requests/songs';

/** Three in a row is the server, not the tracks: skipping on would walk the whole queue. */
const limit = 3;

export type Skip = { track: SearchResult; reason: string };

/**
 * The tracks the player gave up on, for the one notice that lists them. Both
 * engines write here; `SkipNotice.svelte` is the only reader, and closing it is
 * what empties the list.
 */
class Skipped {
	/** What the notice lists, oldest first, one row per track. */
	tracks: Skip[] = $state([]);
	/** The last failure was the one that stopped the skipping. */
	stopped = $state(false);
	#streak = 0;

	/** A track the player gave up on. True when the player should move past it. */
	add(track: SearchResult, reason: string) {
		const known = this.tracks.find((skip) => skip.track.id === track.id);
		if (known) known.reason = reason;
		else this.tracks.push({ track, reason });

		this.stopped = ++this.#streak >= limit;
		return !this.stopped;
	}

	/** A track loaded, so whatever is wrong, it is not everything. */
	loaded() {
		this.#streak = 0;
	}

	dismiss() {
		this.tracks = [];
		this.stopped = false;
	}
}

/** The chip on the row: the status the API answered, or what went wrong instead. */
export function reasonOf(error: unknown) {
	if (error instanceof AudioApiError) return String(error.status);
	if (error instanceof DOMException && error.name === 'EncodingError') return 'decode';
	return 'network';
}

export default new Skipped();
