import { describe, expect, it } from 'vitest';
import { keysOf } from './paging';

const tracks = (...ids: string[]) => ids.map((id) => ({ id }));

describe('keysOf', () => {
	it('tells copies of the same track apart', () => {
		expect(keysOf(tracks('a', 'b', 'a'))).toEqual(['a#0', 'b#0', 'a#1']);
	});

	it('keeps the keys of the rows after one that goes', () => {
		expect(keysOf(tracks('x', 'a', 'b', 'a')).slice(1)).toEqual(keysOf(tracks('a', 'b', 'a')));
	});

	it('keeps a key with its track when the track moves', () => {
		expect(keysOf(tracks('c', 'a', 'b'))).toEqual(['c#0', 'a#0', 'b#0']);
	});
});
