import { describe, expect, it } from 'vitest';
import { landing } from './reorder';

// four 40px rows, centres at 20, 60, 100, 140
const mids = [20, 60, 100, 140];

describe('landing', () => {
	it('stays put until the pointer passes a neighbour’s middle', () => {
		expect(landing(1, 50, mids)).toBe(1);
		expect(landing(1, 75, mids)).toBe(1);
	});

	it('moves down past every middle it crossed', () => {
		expect(landing(0, 101, mids)).toBe(2);
		expect(landing(0, 500, mids)).toBe(3);
	});

	it('moves up past every middle it crossed', () => {
		expect(landing(3, 59, mids)).toBe(1);
		expect(landing(3, -10, mids)).toBe(0);
	});
});
