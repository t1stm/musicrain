import { describe, expect, it, vi } from 'vitest';
import { hold } from './press';

// jsdom has no PointerEvent; a MouseEvent carries every field `hold` reads
const pointer = (type: string, x = 0, y = 0) =>
	new MouseEvent(type, { button: 0, clientX: x, clientY: y, bubbles: true });

describe('hold', () => {
	it('opens after a still press and swallows the click that ends it', () => {
		vi.useFakeTimers();
		const node = document.createElement('button');
		const opened = vi.fn();
		const clicked = vi.fn();
		node.addEventListener('click', clicked);
		hold(opened)(node);

		node.dispatchEvent(pointer('pointerdown'));
		vi.advanceTimersByTime(450);
		expect(opened).toHaveBeenCalledOnce();

		node.dispatchEvent(pointer('pointerup'));
		node.dispatchEvent(new MouseEvent('click', { detail: 1, bubbles: true }));
		expect(clicked).not.toHaveBeenCalled();

		// the next press is a tap again
		node.dispatchEvent(pointer('pointerdown'));
		node.dispatchEvent(pointer('pointerup'));
		node.dispatchEvent(new MouseEvent('click', { detail: 1, bubbles: true }));
		expect(clicked).toHaveBeenCalledOnce();
		vi.useRealTimers();
	});

	it('lets go of a press that moves', () => {
		vi.useFakeTimers();
		const node = document.createElement('button');
		const opened = vi.fn();
		hold(opened)(node);

		node.dispatchEvent(pointer('pointerdown'));
		node.dispatchEvent(pointer('pointermove', 0, 30));
		vi.advanceTimersByTime(1000);
		expect(opened).not.toHaveBeenCalled();
		vi.useRealTimers();
	});
});
