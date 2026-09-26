import type { PageLoad } from './$types';
import { streamArtistDeezer, streamArtistLocal, streamArtistYouTube } from '$requests/songs';

/** Awaits nothing: every side renders as placeholder rows and fills itself in. */
export const load: PageLoad = ({ url, fetch }) => {
	const term = url.searchParams.get('term')?.trim() ?? '';
	if (!term) return { term, streams: null };

	return {
		term,
		streams: {
			library: streamArtistLocal(term, fetch),
			deezer: streamArtistDeezer(term, fetch),
			youtube: streamArtistYouTube(term, fetch)
		}
	};
};
