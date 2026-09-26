import { browser } from '$app/environment';
import { isDiscordActivity } from '$lib/discord';

const latencyKey = 'musicrain.latency-ms';
/** Beyond this it stops being calibration and starts being a broken room. */
const latencyLimitMs = 1000;

class Audio {
	volume: number = $state(0.2);
	currentSeconds: number = $state(0);
	bufferedSeconds: number = $state(0);
	/** Nothing plays before a track is picked, and the pick is a real change: the
	 *  engines start their media on `paused` flipping. Starting at `false` made the
	 *  first `setCurrent` a no-op for them — the gapless engine's keeper, refused
	 *  at mount for want of a gesture, was never asked again, and the lock screen
	 *  controls stayed dark until a pause and a play. */
	paused: boolean = $state(true);
	/** What the player is asked to run at. The room's clock steers this within
	 *  a couple of percent to hold everyone together; nothing else writes it. */
	rate: number = $state(1);

	/** The exact audible position right now, in seconds.
	 *
	 *  `currentSeconds` is a 10 Hz sample of this for the UI, and the room's clock
	 *  must not use that: `SyncClock.sample` subtracts the local position from the
	 *  server's, and picks the winning sample by round trip rather than by
	 *  freshness — so however long ago the display last ticked lands in the sync
	 *  error unfiltered, against a 35 ms deadband. `Audio.svelte` installs the real
	 *  one; this fallback is what runs before the player mounts. */
	positionNow: () => number = () => this.currentSeconds;

	/** Whether the browser is holding this device's output silent. An
	 *  AudioContext that starts suspended is the autoplay policy: everything
	 *  routed through it is silence until a gesture resumes it. `null` until the
	 *  player has built the graph and can answer — a room must not be joined on a
	 *  guess, so the gate waits for a real `false` rather than for "not true". */
	blocked: boolean | null = $state(null);

	/** Resumes the graph. A graph that has never run only resumes from inside a
	 *  click handler, which is the whole point of the gate that calls it; one that
	 *  ran and was interrupted is also asked again on coming back to the foreground
	 *  (below). `Audio.svelte` installs the real one, the same way it installs
	 *  `positionNow`. */
	unblock: () => void = () => {};

	/** What the AudioContext reports for this device's output path, in
	 *  milliseconds — the part no one has to calibrate. Written by the player once
	 *  the graph exists; stays 0 before that, and on a Safari that reports no
	 *  `outputLatency` at all. */
	measuredMs: number = $state(0);

	/** The calibration knob, in milliseconds, on top of what the AudioContext
	 *  reports. Bluetooth adds anywhere from tens to a few hundred milliseconds no
	 *  API can see, and Safari has no `outputLatency` to report at all; positive
	 *  means the sound reaches the ear later than the graph admits. It describes
	 *  this device's output path, so it is remembered on this device. */
	#latencyMs = $state(browser ? this.#stored() : 0);

	#stored() {
		return this.#clamp(Number(localStorage.getItem(latencyKey)));
	}

	// storage is user-editable and an empty number input binds through as null:
	// a NaN here would spread through every position the room syncs against.
	#clamp(value: number) {
		if (!Number.isFinite(value)) return 0;
		return Math.max(-latencyLimitMs, Math.min(latencyLimitMs, Math.round(value)));
	}

	get latencyMs() {
		return this.#latencyMs;
	}

	set latencyMs(value: number) {
		this.#latencyMs = this.#clamp(value);
		if (browser) localStorage.setItem(latencyKey, String(this.#latencyMs));
	}
}

const audio = new Audio();

if (browser) {
	// iOS interrupts every AudioContext as the app goes to the background, and both
	// engines play through one, unless the page says it is a music player. The same
	// switch plays through the silent switch and pauses other apps' audio when this
	// starts, as a native player does. Not inside Discord: the activity lives in
	// Discord's own app, whose voice call holds that audio session.
	if (!isDiscordActivity && 'audioSession' in navigator)
		(navigator as Navigator & { audioSession: { type: string } }).audioSession.type = 'playback';

	// Back in the foreground, iOS does not always let the graph go again (WebKit
	// bug 263627). A resume that is refused leaves `blocked` set, which in a room is
	// the gate asking for the tap that fixes it.
	document.addEventListener('visibilitychange', () => {
		if (!document.hidden) audio.unblock();
	});
}

export default audio;
