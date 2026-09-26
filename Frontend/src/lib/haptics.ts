/**
 * A tick in the hand as a gesture crosses a line: a swipe far enough to land, a swipe row's
 * step, a row picked up or carried past another, a hold that opened a menu. What changes on
 * screen then is under the thumb doing it. `vibrate` is Chromium's — Android — and answers
 * only once the page has had a tap; anywhere else nothing is felt and nothing else changes.
 *
 * Motors differ, so the length is per device: Settings → Advanced sets it, and 0 is off.
 */
let tickMs = 10;

export const setTickMs = (ms: number) => (tickMs = ms);

export const haptic = () => {
	if (tickMs > 0) navigator.vibrate?.(tickMs);
};
