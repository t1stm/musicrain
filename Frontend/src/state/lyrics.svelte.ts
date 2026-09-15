import current from '$states/current.svelte';
import { getLyrics, type Lyrics } from '$requests/lyrics';

const openKey = 'musicrain.lyrics-open';

export type LyricsStatus = 'idle' | 'loading' | 'ready' | 'none' | 'error';

class LyricsState {
	status: LyricsStatus = $state('idle');
	lyrics: Lyrics | null = $state(null);

	/** The line being sung, -1 for none. Written by the pane's ticker, sixty times a
	 *  second at most and only when the value actually changed. */
	activeIndex: number = $state(-1);

	/** Whether the listener wants the pane. Remembered on this device. */
	#open = $state(stored());

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
		remember(value);
		if (value) this.load();
		else this.#cancel();
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
			})
			.catch((error) => {
				if (generation !== this.#generation) return;
				if (error instanceof DOMException && error.name === 'AbortError') return;

				// A lyrics service that is down must not be able to break playback, a room or
				// the transport controls, so the error stops here. The next track tries again.
				this.#loadedId = '';
				this.lyrics = null;
				this.status = 'error';
			});
	}

	#cancel() {
		this.#generation++;
		this.#inFlight?.abort();
		this.#inFlight = null;
	}
}

// No `browser` guard on either of these: server-side rendering, a private window and
// blocked site data all fail the same way — by throwing on access rather than by
// answering — so the try/catch is the guard, and it is the one that covers all three.
function stored() {
	try {
		return localStorage.getItem(openKey) === 'true';
	} catch {
		return false;
	}
}

function remember(value: boolean) {
	try {
		localStorage.setItem(openKey, String(value));
	} catch {
		// A preference that cannot be remembered is still a preference for this session.
	}
}

export default new LyricsState();
