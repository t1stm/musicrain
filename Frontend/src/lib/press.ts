/**
 * Enter and Space for a `role="button"` row, the way a real button takes them. A key pressed
 * on a control inside the row is that control's.
 */
export const pressKeys = (press: () => void) => (event: KeyboardEvent) => {
	if (event.target !== event.currentTarget || (event.key !== 'Enter' && event.key !== ' ')) return;
	event.preventDefault();
	press();
};
