import { expect, test } from 'vitest';
import { rollShares } from './rollSources.svelte';

const both = { youTube: true, deezer: true };

test('the first roll is YouTube only, every later one splits the online share', () => {
	expect(rollShares(60, both, false)).toEqual({ library: 60, youTube: 40, deezer: 0 });
	expect(rollShares(60, both, true)).toEqual({ library: 60, youTube: 20, deezer: 20 });
	expect(rollShares(55, both, true)).toEqual({ library: 55, youTube: 23, deezer: 22 });
});

test('a switched-off source hands its share to the other, or to the library', () => {
	expect(rollShares(60, { youTube: false, deezer: true }, false)).toEqual({ library: 60, youTube: 0, deezer: 40 });
	expect(rollShares(60, { youTube: true, deezer: false }, true)).toEqual({ library: 60, youTube: 40, deezer: 0 });
	expect(rollShares(60, { youTube: false, deezer: false }, true)).toEqual({ library: 100, youTube: 0, deezer: 0 });
});
