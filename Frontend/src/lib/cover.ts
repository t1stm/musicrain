import { audioApi } from './discord';

/**
 * Every track image comes from Snimka, which keeps the library's covers (`cover://…`) and a copy of every
 * remote thumbnail it has been asked for, so the one origin serves them all — the Discord activity's
 * `/.proxy/api` mapping included. `size` is a square in device pixels, cut once on the server and cached:
 * a list of 1200px covers drawn at 40px is what Chromium re-decodes every time a view transition captures
 * the page. Anything that is not a track image (a playlist's own cover, `/empty.png`) is left as it is.
 */
export function cover(url: string | null | undefined, size?: number): string | undefined {
	if (!url) return undefined;
	if (!url.startsWith('cover://') && !/^https?:\/\//.test(url)) return url;

	return `${audioApi}/Cover/Get?url=${encodeURIComponent(url)}${size ? `&size=${size}` : ''}`;
}

/** A row's sleeve: up to 44 CSS pixels on a 3.5x screen. */
export const ROW = 160;

/** A card, or a sleeve bigger than a row's: up to 140 CSS pixels. */
export const CARD = 480;
