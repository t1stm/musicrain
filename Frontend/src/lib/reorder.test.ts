import { describe, expect, it } from 'vitest';
import { edgeSpeed, landing } from './reorder';

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

// a list from 100 to 500 on screen, 56px edges
describe('edgeSpeed', () => {
	it('holds still in the middle', () => {
		expect(edgeSpeed(300, 100, 500)).toBe(0);
		expect(edgeSpeed(156, 100, 500)).toBe(0);
		expect(edgeSpeed(444, 100, 500)).toBe(0);
	});

	it('scrolls up at the top and down at the bottom, faster deeper in', () => {
		expect(edgeSpeed(128, 100, 500)).toBeCloseTo(-0.4);
		expect(edgeSpeed(472, 100, 500)).toBeCloseTo(0.4);
		expect(edgeSpeed(100, 100, 500)).toBeCloseTo(-0.8);
	});

	it('goes no faster past the edge', () => {
		expect(edgeSpeed(-200, 100, 500)).toBeCloseTo(-0.8);
		expect(edgeSpeed(900, 100, 500)).toBeCloseTo(0.8);
	});

	it('keeps a middle on a short list', () => {
		// 120px tall: 30px edges
		expect(edgeSpeed(160, 100, 220)).toBe(0);
		expect(edgeSpeed(115, 100, 220)).toBeCloseTo(-0.4);
	});
});
