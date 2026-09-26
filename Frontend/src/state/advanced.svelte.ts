import { setTickMs } from '$lib/haptics';
import { logSync } from '$lib/syncClock';

/** The lengths a phone's tick can be, in milliseconds. Zero is none at all. */
export const vibrations = [
	{ ms: 0, label: 'Off' },
	{ ms: 5, label: 'Short' },
	{ ms: 10, label: 'Medium' },
	{ ms: 20, label: 'Long' },
] as const;

/**
 * Settings → Advanced. Each of these was a constant with a note asking for a knob; the
 * libraries that use them stay free of state, so a setter hands them the value.
 */
class Advanced {
	/** "Download raw" and "Copy ID" in a track's menu. */
	trackTools = $state(false);

	#vibrationMs = $state(10);
	#logSync = $state(true);

	get vibrationMs() {
		return this.#vibrationMs;
	}

	set vibrationMs(ms: number) {
		this.#vibrationMs = ms;
		setTickMs(ms);
	}

	/** Every step the room's clock takes, in the browser console. */
	get logSync() {
		return this.#logSync;
	}

	set logSync(on: boolean) {
		this.#logSync = on;
		logSync(on);
	}
}

export default new Advanced();
