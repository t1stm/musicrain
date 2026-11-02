const storageKey = 'musicrain.roll-sources';

/** Where the home page's slider starts: the library's share of a roll, in percent. */
export const firstLibraryPercent = 60;

export type RollShares = { library: number; youTube: number; deezer: number };

/**
 * What a roll asks the API for, in whole percents that always add up to 100.
 *
 * The page's first roll draws its online share from YouTube alone; every roll after it splits that share
 * down the middle between YouTube and Deezer, YouTube taking the odd point. A source switched off gives
 * its half to the other one, and with both off the library has the whole roll.
 */
export function rollShares(
	libraryPercent: number,
	on: { youTube: boolean; deezer: boolean },
	split: boolean,
): RollShares {
	const online = on.youTube || on.deezer ? 100 - libraryPercent : 0;
	const youTube = !on.youTube ? 0 : on.deezer && split ? Math.ceil(online / 2) : online;
	return { library: 100 - online, youTube, deezer: online - youTube };
}

/**
 * Which online caches the home page's roll mixes in. Kept per device rather than in the account's
 * settings: a phone on mobile data may want less than the desktop does, and the page's load function
 * reads this before the settings store has restored anything.
 */
class RollSources {
	youTube = $state(true);
	deezer = $state(true);

	constructor() {
		// Server-side rendering, a private window and blocked site data all throw here: both stay on.
		try {
			const saved = JSON.parse(localStorage.getItem(storageKey) ?? '{}');
			if (typeof saved.youTube === 'boolean') this.youTube = saved.youTube;
			if (typeof saved.deezer === 'boolean') this.deezer = saved.deezer;
		} catch {
			// nothing remembered is the default
		}
	}

	toggle(source: 'youTube' | 'deezer') {
		this[source] = !this[source];
		try {
			localStorage.setItem(storageKey, JSON.stringify({ youTube: this.youTube, deezer: this.deezer }));
		} catch {
			// a choice that cannot be remembered still holds for this visit
		}
	}
}

export default new RollSources();
