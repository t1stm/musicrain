import type { Attachment } from 'svelte/attachments';

/**
 * Enter and Space for a `role="button"` row, the way a real button takes them. A key pressed
 * on a control inside the row is that control's.
 */
export const pressKeys = (press: () => void) => (event: KeyboardEvent) => {
	if (event.target !== event.currentTarget || (event.key !== 'Enter' && event.key !== ' ')) return;
	event.preventDefault();
	press();
};

/** How long a press has to stay down before it is a hold. */
const HOLD = 450;

/**
 * A press held still: how a phone asks for a menu. Android and a right-click ask with a
 * `contextmenu` and get the same answer; iOS never sends one for a touch, so a timer asks
 * there. The click that ends a hold is swallowed, so a hold is never also a tap. Moving past
 * a wobble — the row scrolling under the finger — lets the press go.
 */
export const hold =
	(then: () => void): Attachment<HTMLElement> =>
	(node) => {
		let timer: ReturnType<typeof setTimeout> | undefined;
		let held = false;
		let x = 0;
		let y = 0;

		const cancel = () => clearTimeout(timer);
		const fire = () => {
			held = true;
			then();
		};
		const down = (event: PointerEvent) => {
			held = false;
			if (event.button !== 0) return;
			x = event.clientX;
			y = event.clientY;
			timer = setTimeout(fire, HOLD);
		};
		const move = (event: PointerEvent) => {
			if (Math.hypot(event.clientX - x, event.clientY - y) > 10) cancel();
		};
		const menu = (event: MouseEvent) => {
			event.preventDefault();
			cancel();
			fire();
		};
		// a keyboard's click has no press behind it, so it is never the end of a hold
		const click = (event: MouseEvent) => {
			if (!held || event.detail === 0) return;
			held = false;
			event.preventDefault();
			event.stopPropagation();
		};

		node.addEventListener('pointerdown', down);
		node.addEventListener('pointermove', move);
		node.addEventListener('contextmenu', menu);
		node.addEventListener('click', click, true);
		for (const end of ['pointerup', 'pointercancel', 'pointerleave']) node.addEventListener(end, cancel);
		return () => {
			cancel();
			node.removeEventListener('pointerdown', down);
			node.removeEventListener('pointermove', move);
			node.removeEventListener('contextmenu', menu);
			node.removeEventListener('click', click, true);
			for (const end of ['pointerup', 'pointercancel', 'pointerleave']) node.removeEventListener(end, cancel);
		};
	};
