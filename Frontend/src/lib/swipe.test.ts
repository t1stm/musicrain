import { describe, expect, it } from 'vitest';
import { ignored, settle } from './swipe';

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

describe('ignored', () => {
	document.body.innerHTML = `
		<div data-body>
			<section data-handle><p id="handle"></p></section>
			<ul><li id="body"></li></ul>
		</div>
		<p id="outside"></p>`;
	const at = (id: string) => document.getElementById(id)!;

	it('leaves presses inside the ignored part alone', () => {
		expect(ignored(at('body'), '[data-body]', '[data-handle]')).toBe(true);
	});

	it('takes a press on a handle inside the ignored part', () => {
		expect(ignored(at('handle'), '[data-body]', '[data-handle]')).toBe(false);
		expect(ignored(at('handle'), '[data-body]')).toBe(true);
	});

	it('takes everything outside it', () => {
		expect(ignored(at('outside'), '[data-body]', '[data-handle]')).toBe(false);
		expect(ignored(at('outside'))).toBe(false);
	});
});
