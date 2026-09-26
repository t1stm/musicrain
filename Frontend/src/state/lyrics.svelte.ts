import current from '$states/current.svelte';
import { getLyrics, type Lyrics } from '$requests/lyrics';

export type LyricsStatus = 'idle' | 'loading' | 'ready' | 'none' | 'error';

class LyricsState {
	status: LyricsStatus = $state('idle');
	lyrics: Lyrics | null = $state(null);

	/** The line being sung, -1 for none. Written by the pane's ticker, sixty times a
	 *  second at most and only when the value actually changed. */
	activeIndex: number = $state(-1);

	/** Whether the listener wants the pane. Remembered by `settings.svelte`, as `lyricsOpen`. */
	#open = $state(false);

	/** The last answer was that the track has no words. Kept through the next load, so the
	 *  pane neither flashes in for another track without words nor out between two with them. */
	#wordless = $state(false);

	/** Bumped on every load. The answer for the previous track is dropped rather than
	 *  rendered over the current one — skipping tracks quickly is exactly how someone
	 *  ends up watching the wrong song's words, and this class exists to make that
	 *  impossible. */
	#generation = 0;
	#inFlight: AbortController | null = null;
	#loadedId = '';

	get open() {
		return this.#open;
	}

	set open(value: boolean) {
		this.#open = value;
		if (value) {
			// a press on the button is a question, so it gets the answer even when it is "none"
			this.#wordless = false;
			this.load();
		} else this.#cancel();
	}

	/** Whether the pane is on screen: wanted, and not for a track known to have no words.
	 *  The wish outlives such a track, so the next one with words opens the pane again. */
	get shown() {
		return this.#open && !this.#wordless;
	}

	/** Fetches the current track's words, unless they are already the ones we have. */
	load(fetcher: typeof fetch = fetch) {
		const id = current.id;

		// Nothing playing: clear rather than ask. A listener who never opens the pane
		// costs no requests at all, which is why nothing here runs while it is closed.
		if (!id) {
			this.#cancel();
			this.lyrics = null;
			this.activeIndex = -1;
			this.status = 'idle';
			this.#loadedId = '';
			return;
		}

		if (!this.#open) return;
		if (this.#loadedId === id && this.status !== 'error') return;

		this.#cancel();
		const generation = ++this.#generation;
		const controller = new AbortController();
		this.#inFlight = controller;

		this.status = 'loading';
		this.lyrics = null;
		this.activeIndex = -1;

		getLyrics(fetcher, id, controller.signal)
			.then((found) => {
				if (generation !== this.#generation) return;
				this.#loadedId = id;
				this.lyrics = found;
				this.status = found ? 'ready' : 'none';
				this.#wordless = !found;
			})
			.catch((error) => {
				if (generation !== this.#generation) return;
				if (error instanceof DOMException && error.name === 'AbortError') return;

				// A lyrics service that is down must not be able to break playback, a room or
				// the transport controls, so the error stops here. The next track tries again.
				this.#loadedId = '';
				this.lyrics = null;
				this.status = 'error';
				this.#wordless = false;
			});
	}

	#cancel() {
		this.#generation++;
		this.#inFlight?.abort();
		this.#inFlight = null;
	}
}

export default new LyricsState();
