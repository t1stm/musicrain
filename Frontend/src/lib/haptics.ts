/**
 * A tick in the hand as a gesture crosses a line: a swipe far enough to land, a swipe row's
 * step, a row picked up or carried past another, a hold that opened a menu. What changes on
 * screen then is under the thumb doing it. `vibrate` is Chromium's — Android — and answers
 * only once the page has had a tap; anywhere else nothing is felt and nothing else changes.
 *
 * ponytail: one length for every phone, and motors differ. Tune it here.
 */
const TICK_MS = 10;

export const haptic = () => navigator.vibrate?.(TICK_MS);
