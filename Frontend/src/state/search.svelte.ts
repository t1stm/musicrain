import type { SourceKind } from '$requests/history';

export type SearchResult = {
	id: string;
	name: string;
	artist: string;
	album?: string;
	/** Absent on room queue items — those carry the raw platform result. */
	contentUrl?: string;
	duration: string;
	thumbnailUrl: string | null;
	/**
	 * Where it entered the queue from, for the listening history. Not `source`: that is the
	 * platform badge (`lib/source.ts`). Set by the list a track was picked from; absent, the
	 * history files it under `queue`.
	 */
	origin?: { kind: SourceKind; id?: string };
};

const initialState: SearchResult[] = [];
export const searchResults = $state(initialState);
