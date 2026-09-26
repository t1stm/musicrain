import { audioApi } from '$lib/discord';
import { AudioApiError } from './songs';

/** What `Register` and `Login` hand back: who you are and the token that proves it. */
export type Session = {
	username: string;
	token: string;
	/** ISO. Dom slides this forward on use, so a stored copy is a floor — `me` reads the current one. */
	expiresUtc: string;
};

async function send<T>(path: string, init: RequestInit): Promise<T> {
	const response = await fetch(`${audioApi}/Accounts${path}`, init);
	const payload = await response.json().catch(() => null);

	if (!response.ok)
		throw new AudioApiError(
			payload?.error?.message ?? `The audio service returned ${response.status}.`,
			response.status,
		);

	return payload as T;
}

function credentials(username: string, password: string): RequestInit {
	return {
		method: 'POST',
		headers: { 'Content-Type': 'application/json' },
		body: JSON.stringify({ username, password }),
	};
}

/** Dom hashes whatever arrives, so the password goes over the wire as typed — under TLS. */
export function register(username: string, password: string) {
	return send<Session>('/Register', credentials(username, password));
}

export function login(username: string, password: string) {
	return send<Session>('/Login', credentials(username, password));
}

/** Also where the slid expiry is read: the one from `login` only ever understates it. */
export function me(token: string) {
	return send<{ username: string; createdUtc: string; expiresUtc: string }>('/Me', {
		headers: bearer(token),
	});
}

/** Revokes this one token; the same account stays signed in everywhere else. */
export function logout(token: string) {
	return send<null>('/Logout', { method: 'POST', headers: bearer(token) });
}

export function bearer(token: string) {
	return { Authorization: `Bearer ${token}` };
}
