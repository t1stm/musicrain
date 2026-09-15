import { audioApi } from '$lib/discord';
import { AudioApiError } from '$requests/songs';

type Fetcher = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

export type LyricLine = { at: number | null; text: string };

export type Lyrics = {
	type: 'Synchronized' | 'Unsynchronized';
	/** `null` for a file that was already sitting in the library folder. */
	source: 'Deezer' | 'LRCLIB' | null;
	lines: LyricLine[];
	/** The plain block, timestamps stripped. Always present, for both types. */
	text: string;
	/** What the words actually belong to, which is not always what was asked for. */
	matched: { title: string; artist: string | null; length: number } | null;
};

/**
 * The words for one track, or `null` when there are none.
 *
 * One parameter, because `stih` asks the owning pod what the track is called and how
 * long it runs — so nothing here has to stay in step with the backend's matching rules.
 *
 * Built on `audioApi` like every other request module, so the Discord activity reaches
 * it through `/.proxy/api/Audio` with no extra Developer Portal mapping.
 */
export async function getLyrics(
	fetcher: Fetcher,
	id: string,
	signal?: AbortSignal
): Promise<Lyrics | null> {
	const response = await fetcher(`${audioApi}/Lyrics/Get?id=${encodeURIComponent(id)}`, { signal });

	// The ordinary answer for most tracks. Not an error, and never logged as one — and
	// the body is empty, so there is nothing to parse.
	if (response.status === 204) return null;

	if (!response.ok) {
		const payload = await response.json().catch(() => null);
		throw new AudioApiError(
			payload?.error?.message ?? `The audio service returned ${response.status}.`,
			response.status
		);
	}

	return (await response.json()) as Lyrics;
}
