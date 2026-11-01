import { audioApi } from '$lib/discord';
import { staffCall } from './staff';

/**
 * Oko's staff gateway, `/Audio/Admin`: every service's admin surface, read and acted on with the
 * signed-in account's own token. Oko asks Dom who that is, and refuses what the account may not do.
 */

export type Target = { name: string; permission: string | null };

/** Whatever the service chose to report. Each view reads it as its own shape. */
export type Snapshot = Record<string, unknown>;

export type ServiceStatus = {
	name: string;
	url: string;
	/** What an admin needs to act on it; none means the owner alone. */
	permission: string | null;
	up: boolean;
	snapshot: Snapshot | null;
	error: string | null;
};

export type FleetSnapshot = { at: string; services: ServiceStatus[] };

export type RequestRow = {
	at: string;
	method: string;
	path: string;
	status: number;
	ms: number;
};

export type AuditEntry = {
	at: string;
	who: string;
	target: string;
	action: string;
	parameters: string;
	status: number;
	error: string | null;
};

/** Parameters as Oko forwards them: the query string. Repeated keys need `URLSearchParams`. */
export type Params = URLSearchParams | Record<string, string | number | boolean>;

const admin = (path: string) => `${audioApi}/Admin${path}`;
const segment = encodeURIComponent;

function query(params?: Params) {
	if (!params) return '';
	const search =
		params instanceof URLSearchParams
			? params
			: new URLSearchParams(Object.entries(params).map(([key, value]) => [key, String(value)]));
	const text = search.toString();
	return text ? `?${text}` : '';
}

export const targets = () => staffCall<Target[]>(admin('/targets'));

export const snapshot = () => staffCall<FleetSnapshot>(admin('/snapshot'));

export const requests = (name: string) => staffCall<RequestRow[]>(admin(`/requests/${segment(name)}`));

/** One of a target's GET `/Admin/{action}` routes. Not audited. */
export const read = <T>(name: string, action: string, params?: Params) =>
	staffCall<T>(admin(`/read/${segment(name)}/${segment(action)}${query(params)}`));

/** One of a target's POST `/Admin/{action}` routes. Oko audits it, refused or not. */
export const act = <T = unknown>(name: string, action: string, params?: Params) =>
	staffCall<T>(admin(`/action/${segment(name)}/${segment(action)}${query(params)}`), { method: 'POST' });

export const audit = () => staffCall<AuditEntry[]>(admin('/audit'));

/** The merged live feed, as a stream for `sse`. Ends when `signal` aborts or Oko drops it. */
export const events = (signal: AbortSignal) =>
	staffCall<ReadableStream<Uint8Array>>(admin('/events'), { signal }, 'body');
