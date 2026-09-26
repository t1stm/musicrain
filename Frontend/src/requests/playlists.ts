import { audioApi, proxyThumbnails } from '$lib/discord';
import { bearer } from './accounts';
import { AudioApiError } from './songs';
import account from '$states/account.svelte';
import type { SearchResult } from '$states/search.svelte';

type Fetcher = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

/** What a card needs. `coverUrl` is a path on the API, not an absolute URL — see `coverFor`. */
export type PlaylistSummary = {
	id: string;
	name: string;
	owner: string;
	isPublic: boolean;
	trackCount: number;
	/** `hh:mm:ss`, the shape the rest of the API speaks. */
	duration: string;
	coverUrl: string | null;
	firstTrackId: string | null;
	firstTrackThumbnailUrl: string | null;
	createdUtc: string;
	updatedUtc: string;
};

/** A track as it was when it was saved — a field-for-field subset of `SearchResult`. */
export type Playlist = PlaylistSummary & { tracks: SearchResult[] };

async function send<T>(path: string, init: RequestInit = {}, fetcher: Fetcher = fetch): Promise<T> {
	const response = await fetcher(`${audioApi}/Playlists${path}`, init);
	const payload = await response.json().catch(() => null);

	if (!response.ok) {
		// every authenticated call to Dom comes through here, so this is the one
		// place that sees a token stop working — see `Account.reject`
		account.reject(response.status);

		throw new AudioApiError(
			payload?.error?.message ?? `The audio service returned ${response.status}.`,
			response.status,
		);
	}

	return payload as T;
}

function json(token: string, method: string, body: object): RequestInit {
	return {
		method,
		headers: { ...bearer(token), 'Content-Type': 'application/json' },
		body: JSON.stringify(body),
	};
}

/** The tracks come back with the thumbnails they were saved with; inside the activity only
 *  the API's own origin is reachable, so they go through the same proxy every result does. */
function withProxiedTracks(playlist: Playlist): Playlist {
	return { ...playlist, tracks: proxyThumbnails(playlist.tracks) };
}

export function getPublicPlaylists(fetcher: Fetcher = fetch) {
	return send<PlaylistSummary[]>('/Public', {}, fetcher);
}

export function getMyPlaylists(token: string, fetcher: Fetcher = fetch) {
	return send<PlaylistSummary[]>('/Mine', { headers: bearer(token) }, fetcher);
}

/** The token is optional: a public playlist is a link that works for anybody. */
export async function getPlaylist(id: string, token: string | null, fetcher: Fetcher = fetch) {
	const init = token ? { headers: bearer(token) } : {};

	return withProxiedTracks(await send<Playlist>(`/${encodeURIComponent(id)}`, init, fetcher));
}

export type PlaylistEdit = {
	name?: string;
	isPublic?: boolean;
	tracks?: SearchResult[];
};

export async function createPlaylist(token: string, edit: PlaylistEdit) {
	return withProxiedTracks(await send<Playlist>('', json(token, 'POST', edit)));
}

/** A field left out is a field left alone; `tracks`, when sent, replaces the list. */
export async function patchPlaylist(token: string, id: string, edit: PlaylistEdit) {
	return withProxiedTracks(
		await send<Playlist>(`/${encodeURIComponent(id)}`, json(token, 'PATCH', edit)),
	);
}

/** One track onto the end. A track already in the playlist is left where it is, and `added` is false. */
export function appendTrack(token: string, id: string, track: SearchResult) {
	return send<{ added: boolean; playlist: PlaylistSummary }>(
		`/${encodeURIComponent(id)}/Tracks`,
		json(token, 'POST', track),
	);
}

/** PNG, JPEG or WebP, at most 2 MB. Replaces whatever cover the playlist had. */
export async function uploadCover(token: string, id: string, file: File) {
	const body = new FormData();
	body.append('file', file);

	// no Content-Type header: the boundary is the browser's to write
	return send<{ coverUrl: string }>(`/${encodeURIComponent(id)}/Cover`, {
		method: 'PUT',
		headers: bearer(token),
		body,
	});
}

export function deletePlaylist(token: string, id: string) {
	return send<null>(`/${encodeURIComponent(id)}`, { method: 'DELETE', headers: bearer(token) });
}
