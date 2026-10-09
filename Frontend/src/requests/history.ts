import { audioApi, proxyThumbnails } from '$lib/discord';
import type { SearchResult } from '$states/search.svelte';
import { AudioApiError } from './songs';

/** What a play is written as. See Backend/API.md, "Listening history". */
export type PlayBody = {
	trackId: string;
	startedUtc: string;
	utcOffsetMinutes: number;
	durationMs: number;
	playedMs: number;
	startReason: StartReason;
	endReason: EndReason | null;
	sourceKind: SourceKind;
	sourceId: string | null;
	sessionId: string;
	sessionPosition: number;
	platform: 'web' | 'pwa' | 'discord';
	deviceKind: 'mobile' | 'desktop';
	shuffled: boolean;
};

export type StartReason = 'chosen' | 'collection' | 'autoplay' | 'next' | 'previous' | 'room';
export type EndReason = 'finished' | 'skipped' | 'previous' | 'replaced' | 'stopped' | 'error';
export type SourceKind =
	| 'search'
	| 'album'
	| 'artist'
	| 'playlist'
	| 'browse'
	| 'home-roll'
	| 'recent'
	| 'history'
	| 'link'
	| 'queue'
	| 'room';

export type Play = {
	id: string;
	trackId: string;
	startedUtc: string;
	playedMs: number;
	durationMs: number;
	endReason: EndReason | null;
};

const deviceKey = 'musicrain.device-id';

/**
 * A UUID, from `getRandomValues` rather than `randomUUID`: the dev server is opened over
 * plain HTTP on a LAN address, and `randomUUID` only exists in a secure context.
 */
export function uuid() {
	const bytes = crypto.getRandomValues(new Uint8Array(16));
	bytes[6] = (bytes[6] & 0x0f) | 0x40;
	bytes[8] = (bytes[8] & 0x3f) | 0x80;
	const hex = [...bytes].map((byte) => byte.toString(16).padStart(2, '0')).join('');
	return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** Made once per browser and kept: what a signed-out listener's history is filed under. */
export function deviceId() {
	try {
		const stored = localStorage.getItem(deviceKey);
		if (stored) return stored;
		const made = uuid();
		localStorage.setItem(deviceKey, made);
		return made;
	} catch {
		// storage refused: this session is its own device, which is all it can be
		return (fallbackDevice ??= uuid());
	}
}
let fallbackDevice: string | undefined;

function headers(token: string | null, json = false): HeadersInit {
	return {
		'X-Device-Id': deviceId(),
		...(token ? { Authorization: `Bearer ${token}` } : {}),
		...(json ? { 'Content-Type': 'application/json' } : {}),
	};
}

async function send<T>(path: string, init: RequestInit): Promise<T> {
	const response = await fetch(`${audioApi}/History${path}`, init);
	const payload = await response.json().catch(() => null);

	if (!response.ok)
		throw new AudioApiError(
			payload?.error?.message ?? `The history service returned ${response.status}.`,
			response.status,
		);

	return payload as T;
}

/**
 * Writes one play. Answers the status rather than throwing: the outbox decides what each
 * one means, and a network failure is `0`.
 */
export async function putPlay(id: string, body: PlayBody, token: string | null, keepalive = false) {
	try {
		const response = await fetch(`${audioApi}/History/Plays/${id}`, {
			method: 'PUT',
			headers: headers(token, true),
			body: JSON.stringify(body),
			keepalive,
		});
		return response.status;
	} catch {
		return 0;
	}
}

export function listHistory(token: string | null, before?: string | null, limit = 50) {
	const query = new URLSearchParams({ limit: String(limit) });
	if (before) query.set('before', before);
	return send<{ plays: Play[]; next: string | null }>(`?${query}`, { headers: headers(token) });
}

export function recentTracks(token: string | null, limit = 12) {
	return send<{ trackId: string; startedUtc: string }[]>(`/Recent?limit=${limit}`, {
		headers: headers(token),
	});
}

export function claimHistory(token: string) {
	return send<{ claimed: number }>('/Claim', { method: 'POST', headers: headers(token) });
}

export function clearHistory(token: string | null) {
	return send<{ deleted: number }>('', { method: 'DELETE', headers: headers(token) });
}

/**
 * What each ID plays as now, keyed by the ID asked for — the API may answer with another,
 * so the key is the only safe join. IDs nothing resolves are simply absent. Fifty to a
 * request, which is the route's limit.
 */
export async function resolveIds(ids: string[]): Promise<Map<string, SearchResult>> {
	const unique = [...new Set(ids)];
	const answers = await Promise.all(
		Array.from({ length: Math.ceil(unique.length / 50) }, async (_, chunk) => {
			const query = unique
				.slice(chunk * 50, chunk * 50 + 50)
				.map((id) => `id=${encodeURIComponent(id)}`)
				.join('&');
			const response = await fetch(`${audioApi}/Resolve?${query}`);
			if (!response.ok)
				throw new AudioApiError(`The audio service returned ${response.status}.`, response.status);
			return (await response.json()) as { id: string; result: SearchResult }[];
		}),
	);

	const pairs = answers.flat();
	const results = proxyThumbnails(pairs.map((pair) => pair.result));
	return new Map(pairs.map((pair, index) => [pair.id, results[index]]));
}
