/**
 * The admin panel's arithmetic, pure so the tests can hold it to its edges. Ported from Oko's page,
 * where `pagerCheck()` and `batchFieldsCheck()` ran under `?self-check`.
 */

/** Where a page of `take` rows starting at `offset` sits in `total`. */
export function pager(total: number, offset: number, take: number) {
	const pages = Math.max(1, Math.ceil(total / take));
	return {
		page: Math.floor(offset / take) + 1,
		pages,
		last: (pages - 1) * take,
		from: total ? offset + 1 : 0,
		to: Math.min(total, offset + take)
	};
}

/** What a batch field shows when the selection does not agree on it. */
export const MIXED = '—';

/** The value every row agrees on, or `MIXED` when they don't. */
export function shared<T>(rows: T[], read: (row: T) => string): string {
	const first = read(rows[0]);
	return rows.every((row) => read(row) === first) ? first : MIXED;
}

export type BatchValues = { titles: string; artists: string; album: string };

/**
 * The query every selected song is about to be sent, from what the panel opened with (`before`)
 * and what is in the boxes now (`after`). The only place the "untouched means untouched" rule
 * lives: an untouched field sends nothing at all, an emptied one sends an empty value.
 */
export function batchFields(
	before: BatchValues,
	after: BatchValues
): { fields: URLSearchParams; summary: string[]; error?: string } {
	const fields = new URLSearchParams();
	const summary: string[] = [];
	const list = (value: string) =>
		value
			.split('\n')
			.map((v) => v.trim())
			.filter(Boolean);

	if (after.titles !== before.titles) {
		const titles = list(after.titles);
		if (!titles.length)
			return {
				fields,
				summary,
				error: 'A song needs at least one title. Type one, or put back what was there.'
			};
		for (const title of titles) fields.append('title', title);
		summary.push(`titles → ${titles.join(' / ')}`);
	}

	if (after.artists !== before.artists) {
		const artists = list(after.artists);
		// an empty artist parameter is how the service is told to clear the list; sending nothing
		// would mean "leave it alone", the opposite of what an emptied box asked for
		for (const artist of artists.length ? artists : ['']) fields.append('artist', artist);
		summary.push(artists.length ? `artists → ${artists.join(' / ')}` : 'artists cleared');
	}

	const album = after.album.trim();
	if (album !== before.album.trim()) {
		fields.append('album', album);
		summary.push(album ? `album → ${album}` : 'album cleared');
	}

	return { fields, summary };
}

/**
 * How long ago (or until) an instant, as a magnitude: the caller supplies "ago" or "in".
 * Absolute, so it reads the same for an expiry as for a timestamp in the past.
 */
export function ago(iso: string | number, now = Date.now()): string {
	const s = Math.abs(Math.round((now - new Date(iso).getTime()) / 1000));
	if (!isFinite(s)) return '';
	if (s < 60) return `${s}s`;
	if (s < 3600) return `${Math.round(s / 60)}m`;
	return `${Math.round(s / 3600)}h`;
}

export function bytes(n: number | null | undefined): string {
	if (!n) return '0 B';
	const units = ['B', 'KB', 'MB', 'GB', 'TB'];
	const i = Math.min(Math.floor(Math.log(n) / Math.log(1024)), units.length - 1);
	return `${(n / 1024 ** i).toFixed(i ? 1 : 0)} ${units[i]}`;
}

/**
 * A request path as a person reads it: a Cyrillic search is a wall of %D0%A0 otherwise. Malformed
 * escaping is left as it came, since it is still worth reading and `decodeURIComponent` throws on it.
 */
export function dec(path: string): string {
	try {
		return decodeURIComponent(path);
	} catch {
		return path;
	}
}

/** `1:02:03.456` or `00:03:12.1` as the services send a TimeSpan, without the fraction. */
export const clock = (span: string | null | undefined) => (span ? span.split('.')[0] : '—');

/** The colour class for an HTTP status, by its first digit. */
export const statusTone = (status: number) =>
	status >= 500
		? 'text-ember'
		: status >= 400
			? 'text-gold'
			: status >= 200 && status < 300
				? 'text-primary-500'
				: 'text-fog';
