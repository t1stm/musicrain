import type { PageLoad } from './$types';
import { getRandomSongs, streamArtistLocal, streamRandomSongs } from '$requests/songs';
import { heroArtist } from '$lib/artists';
import rollSources, { firstLibraryPercent, rollShares } from '$states/rollSources.svelte';

/**
 * Awaits nothing, so the route renders on the first frame and every section fills
 * itself in. The three requests also go out together now — the hero used to gate
 * them, which cost a whole round trip before anything was even asked for.
 *
 * A failed request has to be caught here: an awaited load lands on the error page,
 * but an unawaited promise would surface as an unhandled rejection. The generators
 * are caught by the page instead, where the section they belong to can be emptied.
 */
export const load: PageLoad = ({ fetch }) => {
	// The first roll of a visit never draws from Deezer unless YouTube is switched off; the page's own
	// rolls split the online share between the two — see rollShares.
	const first = rollShares(firstLibraryPercent, rollSources, false);
	const hero = getRandomSongs(fetch, 1, first.youTube / 100, first.deezer / 100)
		.then((songs) => songs[0] ?? null)
		.catch(() => null);

	return {
		hero,
		picks: streamRandomSongs(fetch, 30, first.youTube / 100, first.deezer / 100),
		librarySongs: streamRandomSongs(fetch, 200),
		// needs the hero's name, so it is the one thing that still chains — but it no
		// longer holds up anything else
		artistSongs: hero.then((song) => (song ? streamArtistLocal(heroArtist(song.artist), fetch) : null))
	};
};
