import { describe, expect, it } from 'vitest';
import { activeIndexAt } from './lyrics';
import type { LyricLine } from '$requests/lyrics';

const song: LyricLine[] = [
	{ at: 0, text: '' },
	{ at: 12.34, text: 'Alle warten auf das Licht' },
	{ at: 20, text: 'fürchtet euch fürchtet euch nicht' },
	{ at: 30.5, text: 'die Sonne scheint mir aus den Augen' }
];

describe('activeIndexAt', () => {
	it('is -1 before the first line, which is the intro and not line zero', () => {
		expect(activeIndexAt([{ at: 5, text: 'late start' }], 0)).toBe(-1);
		expect(activeIndexAt([{ at: 5, text: 'late start' }], 4.999)).toBe(-1);
	});

	// The boundary that matters: off by one here means every line flips a frame early
	// for the whole song.
	it('takes the line exactly on its own timestamp', () => {
		expect(activeIndexAt(song, 12.34)).toBe(1);
		expect(activeIndexAt(song, 20)).toBe(2);
	});

	it('holds the previous line between two timestamps', () => {
		expect(activeIndexAt(song, 19.999)).toBe(1);
		expect(activeIndexAt(song, 12.35)).toBe(1);
	});

	it('holds the last line after it, for as long as the song runs', () => {
		expect(activeIndexAt(song, 30.5)).toBe(3);
		expect(activeIndexAt(song, 600)).toBe(3);
	});

	it('answers -1 for an empty song rather than throwing', () => {
		expect(activeIndexAt([], 10)).toBe(-1);
		expect(activeIndexAt([], 0)).toBe(-1);
	});

	it('handles a single-line file at both ends', () => {
		const one: LyricLine[] = [{ at: 10, text: 'only' }];
		expect(activeIndexAt(one, 9.9)).toBe(-1);
		expect(activeIndexAt(one, 10)).toBe(0);
		expect(activeIndexAt(one, 11)).toBe(0);
	});
});
