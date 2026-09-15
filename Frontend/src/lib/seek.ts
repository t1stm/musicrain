import audio from '$states/audio.svelte';
import session from '$states/session.svelte';

/**
 * Move playback to a position, wherever the listener is.
 *
 * Outside a room the element's own clock is the truth and is written directly; inside
 * one the room's is, so the seek is a message and everybody moves together. Two callers
 * now — the seek bar's drag and a lyric line's click — which is why the rule lives here
 * instead of in the bar.
 */
export function seekTo(seconds: number) {
	if (!session.inRoom) {
		audio.currentSeconds = seconds;
		return;
	}

	session.send(`seek ${seconds}`);
}
