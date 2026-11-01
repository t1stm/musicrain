import { expect, it } from 'vitest';
import { MIXED, ago, batchFields, bytes, dec, pager, shared } from './admin';

it('pages a table, edges included', () => {
	expect(pager(0, 0, 100)).toMatchObject({ pages: 1, last: 0, from: 0, to: 0 });
	expect(pager(200, 100, 100)).toMatchObject({
		page: 2,
		pages: 2,
		last: 100,
		to: 200
	});
	expect(pager(908, 900, 100)).toMatchObject({
		page: 10,
		pages: 10,
		from: 901,
		to: 908
	});
	expect(pager(101, 0, 100).last).toBe(100);
});

const before = { titles: 'A', artists: 'B', album: 'C' };
const query = (after: Partial<typeof before>) =>
	batchFields(before, { ...before, ...after }).fields.toString();

it('writes only what the batch editor changed', () => {
	expect(query({})).toBe('');
	expect(batchFields(before, before).summary).toEqual([]);
	expect(query({ album: '' })).toBe('album=');
	expect(query({ artists: '' })).toBe('artist=');
	expect(batchFields(before, { ...before, titles: '' }).error).toBeTruthy();
	expect(query({ artists: ' X \n\n Y ' })).toBe('artist=X&artist=Y');
	expect(query({ titles: 'T', album: '  C  ' })).toBe('title=T');
});

it('leaves a selection that does not agree alone', () => {
	const mixed = { titles: 'A', artists: MIXED, album: MIXED };
	expect(batchFields(mixed, mixed).summary).toEqual([]);
	expect(shared([{ a: '1' }, { a: '2' }], (row) => row.a)).toBe(MIXED);
	expect(shared([{ a: '1' }, { a: '1' }], (row) => row.a)).toBe('1');
});

it('formats times, sizes and paths', () => {
	const now = Date.parse('2026-11-01T12:00:00Z');
	expect(ago('2026-11-01T11:59:30Z', now)).toBe('30s');
	expect(ago('2026-11-01T12:05:00Z', now)).toBe('5m');
	expect(ago('2026-11-01T09:00:00Z', now)).toBe('3h');
	expect(ago('nonsense', now)).toBe('');
	expect(bytes(0)).toBe('0 B');
	expect(bytes(1536)).toBe('1.5 KB');
	expect(dec('/Audio/Search?query=%D0%A0')).toBe('/Audio/Search?query=Р');
	expect(dec('/bad%E0%A4%A')).toBe('/bad%E0%A4%A');
});
