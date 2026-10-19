import type { SearchResult } from '$states/search.svelte';
import { audioApi } from '$lib/discord';
import { streamResults } from './songs';

export async function getSearch(
	term: string,
	fetch: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>
) {
	const r = await fetch(`${audioApi}/Search?query=${encodeURIComponent(term)}`);
	if (!r.ok) throw new Error(`The audio service returned ${r.status}.`);
	const data = await r.json();

	return data as SearchResult[];
}

/** The same search, one result at a time, for a page that fills in as they arrive. */
export function streamSearch(
	term: string,
	fetch: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>
) {
	return streamResults(fetch, `/Search?query=${encodeURIComponent(term)}`);
}
