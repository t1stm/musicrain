import { expect, it } from 'vitest';
import audio from './audio.svelte';

// The knob feeds every position the room syncs against, so nothing unusable may
// get in: an empty number input binds through as null, and localStorage is a
// text field the user can edit by hand.
it('keeps the latency knob to a usable number of milliseconds', () => {
	audio.latencyMs = 120.4;
	expect(audio.latencyMs).toBe(120);

	audio.latencyMs = null as unknown as number;
	expect(audio.latencyMs).toBe(0);

	audio.latencyMs = -9000;
	expect(audio.latencyMs).toBe(-1000);
});

// The room gate joins on `blocked === false` and nothing else. Starting this at
// `false` would look harmless and quietly restore the race it exists to lose:
// the page's effects run before the player has built the graph and can answer.
it('starts with the autoplay block unanswered', () => {
	expect(audio.blocked).toBeNull();
});

// The engines start their media when `paused` flips. Starting at `false`, the first
// track's `paused = false` changed nothing and the media session never started.
it('starts paused, so the first track is a change', () => {
	expect(audio.paused).toBe(true);
});
