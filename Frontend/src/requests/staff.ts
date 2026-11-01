import { audioApi } from '$lib/discord';
import account from '$states/account.svelte';
import type { Role } from './accounts';
import { AudioApiError } from './songs';

/** `RequestInit`, except that a plain object as the body goes as JSON. */
type StaffInit = Omit<RequestInit, 'body'> & { body?: BodyInit | Record<string, unknown> | null };

const isPlain = (body: unknown): body is Record<string, unknown> =>
	typeof body === 'object' && body !== null && Object.getPrototypeOf(body) === Object.prototype;

/**
 * A request on behalf of the signed-in account to a staff-only route: Dom's Staff routes, Oko,
 * Lirika. One copy of what all of them need, so they cannot drift apart. Throws `AudioApiError`.
 */
export async function staffCall<T>(
	url: string,
	init: StaffInit = {},
	as: 'json' | 'blob' | 'body' = 'json'
): Promise<T> {
	const token = account.token;
	if (!token) throw new AudioApiError('Sign in first.', 401);

	const headers = new Headers(init.headers);
	headers.set('Authorization', `Bearer ${token}`);
	let body = init.body;
	if (isPlain(body)) {
		headers.set('Content-Type', 'application/json');
		body = JSON.stringify(body);
	}

	const response = await fetch(url, { ...init, headers, body });
	if (!response.ok) {
		const payload = await response.json().catch(() => null);
		// the stack's envelope, or the bare string studio.py answers with
		const error = payload?.error;
		const message =
			typeof error === 'string'
				? error
				: (error?.message ?? `The audio service returned ${response.status}.`);
		account.reject(response.status);
		throw new AudioApiError(message, response.status);
	}

	if (response.status === 204) return null as T;
	// 'body' hands over the stream itself, for a response that never ends: an SSE feed
	if (as === 'body') return response.body as T;
	return (as === 'blob' ? response.blob() : response.json()) as Promise<T>;
}

// ── Dom's Staff routes ───────────────────────────────────────────────────────────────────

/** A permission, with the line the owner's Staff page shows for it. The list comes from Dom. */
export type Permission = { name: string; description: string };

/** `permissions` is the effective set: every one of them for the owner. */
export type StaffMember = { username: string; role: Role; permissions: string[] };

export type StaffList = { permissions: Permission[]; staff: StaffMember[]; keyWaiting: boolean };

const staffRoute = (path = '') => `${audioApi}/Accounts/Staff${path}`;
const member = (username: string) => staffRoute(`/${encodeURIComponent(username)}`);

/** Spends the administrator key: the owner if there is none yet, an admin who can only look otherwise. */
export function claim(key: string) {
	return staffCall<{ role: Role; permissions: string[] }>(staffRoute('/Claim'), {
		method: 'POST',
		body: { key }
	});
}

export function listStaff() {
	return staffCall<StaffList>(staffRoute());
}

/** Makes an account an admin if it is not one yet, then replaces its permissions. */
export function grant(username: string, permissions: string[]) {
	return staffCall<StaffMember>(member(username), { method: 'PUT', body: { permissions } });
}

export function demote(username: string) {
	return staffCall<null>(member(username), { method: 'DELETE' });
}
