import { beforeEach, describe, expect, it } from 'vitest';
import skipped, { reasonOf } from './skipped.svelte';
import { AudioApiError } from '$requests/songs';
import type { SearchResult } from '$states/search.svelte';

function track(id: string): SearchResult {
	return { id, name: id, artist: 'artist', contentUrl: '', duration: '0:10', thumbnailUrl: null };
}

beforeEach(() => {
	skipped.dismiss();
	skipped.loaded();
});

describe('add', () => {
	it('keeps one row per track, with the latest reason', () => {
		skipped.add(track('a'), '503');
		skipped.loaded();
		skipped.add(track('a'), 'decode');
		expect(skipped.tracks).toEqual([{ track: track('a'), reason: 'decode' }]);
	});

	it('stops skipping on the third failure in a row', () => {
		expect(skipped.add(track('a'), '503')).toBe(true);
		expect(skipped.add(track('b'), '503')).toBe(true);
		expect(skipped.add(track('c'), '503')).toBe(false);
		expect(skipped.stopped).toBe(true);
	});

	it('starts the streak over once a track loads', () => {
		skipped.add(track('a'), '503');
		skipped.add(track('b'), '503');
		skipped.loaded();
		expect(skipped.add(track('c'), '503')).toBe(true);
		expect(skipped.stopped).toBe(false);
	});
});

describe('dismiss', () => {
	it('empties the list and clears stopped', () => {
		skipped.add(track('a'), '503');
		skipped.add(track('b'), '503');
		skipped.add(track('c'), '503');
		skipped.dismiss();
		expect(skipped.tracks).toEqual([]);
		expect(skipped.stopped).toBe(false);
	});
});

describe('reasonOf', () => {
	it('names the status, a decode failure, or the network', () => {
		expect(reasonOf(new AudioApiError('bad gateway', 502))).toBe('502');
		expect(reasonOf(new DOMException('unreadable', 'EncodingError'))).toBe('decode');
		expect(reasonOf(new TypeError('Failed to fetch'))).toBe('network');
	});
});
