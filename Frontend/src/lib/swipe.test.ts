import { describe, expect, it } from 'vitest';
import { settle } from './swipe';

describe('settle', () => {
	it('takes a long drag at any speed', () => {
		expect(settle(0, 120, 900)).toBe('down');
		expect(settle(-90, 0, 900)).toBe('left');
	});

	it('takes a short flick', () => {
		expect(settle(0, -40, 50)).toBe('up');
		expect(settle(30, 0, 40)).toBe('right');
	});

	it('springs back from a short, slow drag', () => {
		expect(settle(0, 40, 400)).toBeNull();
		expect(settle(20, 0, 10)).toBeNull();
	});
});
