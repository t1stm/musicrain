import type { Attachment } from 'svelte/attachments';

/**
 * Closes a popover once a press or the focus lands anywhere outside it — the light dismiss
 * a native `popover` has and these hand-placed panels did not. Back and Escape are
 * `closeOnBack`'s; this is the third way out.
 *
 * Goes on the box that holds the trigger as well as the panel, so pressing the trigger
 * toggles instead of closing and reopening. Conditional, so a page of rows is not a page of
 * document listeners: `{@attach open && dismiss(() => (open = false))}`.
 *
 * The press still reaches whatever it landed on. A menu is not a modal: tapping the next
 * row's menu should open that one, not spend the tap on closing this one.
 */
export const dismiss =
	(close: () => void): Attachment<HTMLElement> =>
	(node) => {
		const outside = (event: Event) => {
			if (!node.contains(event.target as Node)) close();
		};
		// capture: a control that stops propagation still counts as somewhere else
		document.addEventListener('pointerdown', outside, true);
		document.addEventListener('focusin', outside);
		return () => {
			document.removeEventListener('pointerdown', outside, true);
			document.removeEventListener('focusin', outside);
		};
	};
