<script lang="ts">
	import type { Snippet } from 'svelte';
	import { untrack } from 'svelte';
	import { cubicOut } from 'svelte/easing';
	import { MediaQuery } from 'svelte/reactivity';
	import { fade, scale } from 'svelte/transition';
	import { closeOnBack } from '$lib/backWatcher.svelte';

	// The app's one menu. It lifts what it belongs to out, on every screen: a copy of the
	// `data-preview` around it — the row or the card, exactly as drawn — carried from its place
	// to the middle of the screen, the actions under it, and carried back into the gap it left
	// when it closes. `children` are the actions, drawn with the classes it hands them.
	let {
		open = $bindable(false),
		label,
		children
	}: {
		open?: boolean;
		label: string;
		/** The rows, given the row class, icon size and list class the frame draws them with. */
		children: Snippet<[item: string, size: string, list: string]>;
	} = $props();

	const still = new MediaQuery('prefers-reduced-motion: reduce');

	closeOnBack(
		() => open,
		() => (open = false)
	);

	const close = () => (open = false);

	/** The row or card the lift copies, and the gap the copy goes back into. */
	let source: HTMLElement | null = null;

	/**
	 * Opens the lift. On the body, not where it was written: in the row a press on it would
	 * still reach the row's click (a play), its swipe and its reorder. `showModal` puts it in
	 * the top layer, so nothing the row sits in can clip it or make itself its containing
	 * block, and it makes the page behind it inert.
	 */
	const lift = (dialog: HTMLDialogElement) =>
		untrack(() => {
			source = dialog.parentElement?.closest<HTMLElement>('[data-preview]') ?? null;
			const before = document.activeElement as HTMLElement | null;
			document.body.append(dialog);
			dialog.showModal();

			const slot = dialog.querySelector<HTMLElement>('[data-lift]');
			if (source && slot) {
				const from = source.getBoundingClientRect();
				const copy = source.cloneNode(true) as HTMLElement;
				copy.inert = true;
				copy.style.width = `${from.width}px`;
				// the copy's own "…" is the one part of it that is not the track; hidden, not
				// removed, so the row keeps the shape it had in the list
				for (const menu of copy.querySelectorAll('details')) menu.style.visibility = 'hidden';
				slot.append(copy);
				source.style.visibility = 'hidden';

				// Drawn where it ends up, then carried there from where it was: the frame
				// fills in on the way, so the row becomes a card as it leaves the list. A row
				// wider than the card narrows on the way, what is in it moving with it. The
				// lift stays centred while it does, so the start is put back by half of that.
				const to = copy.getBoundingClientRect();
				const pad = slot.getBoundingClientRect().width - to.width;
				if (!still.current)
					slot.animate(
						[
							{
								translate: `${from.left - to.left + (from.width - to.width) / 2}px ${from.top - to.top}px`,
								width: `${from.width + pad}px`,
								maxWidth: 'none',
								backgroundColor: 'transparent',
								borderColor: 'transparent'
							},
							{ translate: '0 0', width: `${to.width + pad}px`, maxWidth: 'none' }
						],
						{ duration: 380, easing: 'cubic-bezier(0.2, 0.7, 0.3, 1)' }
					);
			}

			// Out of the body with it, whatever took the menu away. Closing it plays the way home
			// and then removes it, but a menu gone with its page — a link followed from inside
			// it — is removed from where it was written, which the dialog left: it would stay
			// open over the next page, its shade answering to a menu that no longer exists.
			return () => {
				dialog.remove();
				if (source) source.style.visibility = '';
				before?.focus({ preventScroll: true });
			};
		});

	/**
	 * The way back: from the middle of the screen into the gap it left, the frame going as it
	 * lands, and widening back to the row's width if the card narrowed it (see `lift`).
	 */
	function home(slot: HTMLElement) {
		const copy = slot.firstElementChild as HTMLElement | null;
		if (still.current || !copy || !source?.isConnected)
			return { duration: 150, css: (t: number) => `opacity: ${t}` };

		const from = copy.getBoundingClientRect();
		const to = source.getBoundingClientRect();
		const pad = slot.getBoundingClientRect().width - from.width;
		copy.style.width = `${to.width}px`;
		const x = to.left - from.left + (to.width - from.width) / 2;
		const y = to.top - from.top;
		return {
			duration: 300,
			easing: cubicOut,
			css: (t: number, u: number) => `
				translate: ${u * x}px ${u * y}px;
				width: ${from.width + pad + u * (to.width - from.width)}px;
				max-width: none;
				background-color: color-mix(in srgb, var(--color-surface-100) ${t * 100}%, transparent);
				border-color: color-mix(in srgb, var(--color-haze) ${t * 100}%, transparent);`
		};
	}
</script>

<!-- Escape and Android's back ask the dialog to close before they reach the back stack, so
     the dialog says yes through `open` rather than closing itself under the lift's motion.
     A long press that opened it may still be down; it selects nothing and opens no menu —
     except in a field, which needs its menu to paste. -->
{#if open}
	<dialog
		{@attach lift}
		aria-label={label}
		class="m-0 size-full max-h-none max-w-none select-none overflow-y-auto overscroll-contain bg-transparent p-[env(safe-area-inset-top)_env(safe-area-inset-right)_env(safe-area-inset-bottom)_env(safe-area-inset-left)] text-chalk backdrop:bg-transparent"
		oncancel={(event) => {
			event.preventDefault();
			close();
		}}
		oncontextmenu={(event) => {
			if (!(event.target as Element).closest('input')) event.preventDefault();
		}}
	>
		<!-- the way out a tap expects; back and Escape work too. Named, like everything else
		     drawn in the lift: Firefox leaves the top layer out of a step's snapshot of the
		     page, and the shade went with it until the step was over. Fixed, so it still
		     covers the notch and the gesture bar the dialog's padding keeps the lift out of. -->
		<div
			class="fixed inset-0 bg-dark-0/75"
			style:view-transition-name={open ? 'track-menu-shade' : undefined}
			aria-hidden="true"
			onclick={close}
			transition:fade={{ duration: 220 }}
		></div>
		<!-- Taps anywhere but the actions fall through to the shade, the copy included. -->
		<div class="pointer-events-none relative flex min-h-full items-center justify-center p-2">
			<div class="grid w-fit max-w-full justify-items-center gap-2.5">
				<!-- named, so a step that re-centres the lift carries the track rather than
				     fading it from one place to the other. No wider than a big phone's row: a
				     desktop row is the page's width, and the actions take the copy's. -->
				<div
					data-lift
					class="max-w-lg rounded-panel border border-haze bg-surface-100 p-1.5 [&>*]:max-w-full"
					style:view-transition-name={open ? 'track-menu-lift' : undefined}
					out:home
				></div>
				<!-- 52px a row and a rule between each: a thumb aimed at one lands on one -->
				<div
					class="pointer-events-auto grid w-full min-w-64 origin-top overflow-hidden rounded-panel border border-haze bg-surface-100 text-base"
					style:view-transition-name={open ? 'track-menu' : undefined}
					in:scale={{ start: still.current ? 1 : 0.92, duration: 260, delay: 90, easing: cubicOut }}
					out:fade={{ duration: 120 }}
				>
					{@render children(
						'flex min-h-13 w-full items-center gap-3.5 px-4 text-left outline-none hover:bg-surface-200 focus-visible:bg-surface-200 active:transform-none active:bg-surface-200',
						'18',
						'divide-y divide-haze'
					)}
				</div>
			</div>
		</div>
	</dialog>
{/if}
