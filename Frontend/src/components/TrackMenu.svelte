<script lang="ts">
	import {
		ArrowDownTray,
		Check,
		ClipboardDocument,
		EllipsisHorizontal,
		Icon,
		QueueList,
		User
	} from 'svelte-hero-icons';
	import { untrack } from 'svelte';
	import { cubicOut } from 'svelte/easing';
	import { MediaQuery } from 'svelte/reactivity';
	import { fade, scale } from 'svelte/transition';
	import { resolve } from '$app/paths';
	import { heroArtist } from '$lib';
	import { closeOnBack } from '$lib/backWatcher.svelte';
	import { dismiss } from '$lib/dismiss';
	import type { SearchResult } from '$states/search.svelte';
	import queue from '$states/queue.svelte';

	// A track's menu. Bindable so a swipe on a row or a held press on a card can open it
	// without the "…". `trigger={false}` leaves the "…" out for a caller that opens it itself.
	//
	// Wider than a phone it drops from the "…". On a phone, and anywhere without a "…" to
	// drop from, it lifts the track out instead: a copy of the `data-preview` around the menu
	// — the row or the card, exactly as drawn — carried from its place to the middle of the
	// screen, the actions under it, and carried back into the gap it left when it closes.
	let {
		result,
		open = $bindable(false),
		trigger = true,
		class: className = ''
	}: { result: SearchResult; open?: boolean; trigger?: boolean; class?: string } = $props();

	const phone = new MediaQuery('width < 40rem');
	const still = new MediaQuery('prefers-reduced-motion: reduce');
	let lifted = $derived(open && (phone.current || !trigger));

	// The menu has room for one artist, so it takes the first of a joined credit — the rest are
	// each their own link on the row itself.
	let artistUrl = $derived(
		`${resolve('/artist')}?term=${encodeURIComponent(heroArtist(result.artist))}`
	);

	closeOnBack(
		() => open,
		() => (open = false)
	);

	const close = () => (open = false);

	// An open menu over an action that happened somewhere else reads as nothing
	// happening — on a phone the menu covers the screen.
	function playNext() {
		queue.playNext(result);
		open = false;
	}

	let copied = $state(false);
	let settle: ReturnType<typeof setTimeout>;

	async function copyId() {
		await navigator.clipboard.writeText(result.id);
		// nothing about a copy is visible anywhere else, so the menu stays open
		// long enough to say so and then takes itself away
		copied = true;
		clearTimeout(settle);
		settle = setTimeout(() => {
			copied = false;
			open = false;
		}, 900);
	}

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

				// drawn where it ends up, then carried there from where it was: the frame
				// fills in on the way, so the row becomes a card as it leaves the list
				const to = copy.getBoundingClientRect();
				if (!still.current)
					slot.animate(
						[
							{
								translate: `${from.left - to.left}px ${from.top - to.top}px`,
								backgroundColor: 'transparent',
								borderColor: 'transparent'
							},
							{ translate: '0 0' }
						],
						{ duration: 380, easing: 'cubic-bezier(0.2, 0.7, 0.3, 1)' }
					);
			}

			return () => {
				if (source) source.style.visibility = '';
				before?.focus({ preventScroll: true });
			};
		});

	/** The way back: from the middle of the screen into the gap it left, the frame going as it lands. */
	function home(slot: HTMLElement) {
		const copy = slot.firstElementChild;
		if (still.current || !copy || !source?.isConnected)
			return { duration: 150, css: (t: number) => `opacity: ${t}` };

		const from = copy.getBoundingClientRect();
		const to = source.getBoundingClientRect();
		const x = to.left - from.left;
		const y = to.top - from.top;
		return {
			duration: 300,
			easing: cubicOut,
			css: (t: number, u: number) => `
				translate: ${u * x}px ${u * y}px;
				background-color: color-mix(in srgb, var(--color-surface-100) ${t * 100}%, transparent);
				border-color: color-mix(in srgb, var(--color-haze) ${t * 100}%, transparent);`
		};
	}
</script>

{#if trigger}
	<details
		class="relative {className}"
		bind:open
		{@attach open && !lifted && dismiss(close)}
	>
		<summary
			aria-label="More actions for {result.name}"
			class="flex size-11 list-none items-center justify-center rounded-[5px] border border-haze text-fog hover:bg-surface-200 hover:text-chalk focus-visible:outline-2 focus-visible:outline-primary-200 sm:size-7 [&::-webkit-details-marker]:hidden"
		>
			<Icon src={EllipsisHorizontal} mini size="16" />
		</summary>
		{#if !lifted}
			<div
				class="absolute right-0 z-20 mt-1 grid w-44 gap-0.5 rounded-panel border border-haze bg-surface-100 p-1 text-left text-xs"
			>
				{@render actions(
					'flex min-h-10 items-center gap-2 rounded-art px-2 text-left hover:bg-surface-200',
					'14'
				)}
			</div>
		{/if}
	</details>
{/if}

<!-- Escape and Android's back ask the dialog to close before they reach the back stack, so
     the dialog says yes through `open` rather than closing itself under the lift's motion.
     A long press that opened it may still be down; it selects nothing and opens no menu. -->
{#if lifted}
	<dialog
		{@attach lift}
		aria-label={result.name}
		class="m-0 size-full max-h-none max-w-none select-none overflow-y-auto overscroll-contain bg-transparent p-0 text-chalk backdrop:bg-transparent"
		oncancel={(event) => {
			event.preventDefault();
			close();
		}}
		oncontextmenu={(event) => event.preventDefault()}
	>
		<!-- the way out a tap expects; back and Escape work too -->
		<div
			class="fixed inset-0 bg-dark-0/75"
			aria-hidden="true"
			onclick={close}
			transition:fade={{ duration: 220 }}
		></div>
		<!-- Taps anywhere but the actions fall through to the shade, the copy included. -->
		<div class="pointer-events-none relative flex min-h-full items-center justify-center p-2">
			<div class="grid w-fit max-w-full justify-items-center gap-2.5">
				<div
					data-lift
					class="max-w-full rounded-panel border border-haze bg-surface-100 p-1.5 [&>*]:max-w-full"
					out:home
				></div>
				<!-- 52px a row and a rule between each: a thumb aimed at one lands on one -->
				<div
					class="pointer-events-auto grid w-full min-w-64 origin-top divide-y divide-haze overflow-hidden rounded-panel border border-haze bg-surface-100 text-base"
					in:scale={{ start: still.current ? 1 : 0.92, duration: 260, delay: 90, easing: cubicOut }}
					out:fade={{ duration: 120 }}
				>
					{@render actions(
						'flex min-h-13 items-center gap-3.5 px-4 text-left outline-none hover:bg-surface-200 focus-visible:bg-surface-200 active:transform-none active:bg-surface-200',
						'18'
					)}
				</div>
			</div>
		</div>
	</dialog>
{/if}

<!-- One list, two sizes. Play next wears the primary colour of its own swipe key. -->
{#snippet actions(item: string, size: string)}
	<button type="button" class={item} onclick={playNext}>
		<Icon src={QueueList} mini {size} class="shrink-0 text-primary-500" /> Play next
	</button>
	<!-- room queue items carry no contentUrl; hide the action rather than
	     linking nowhere -->
	{#if result.contentUrl}
		<a href={result.contentUrl} download class={item} onclick={close}>
			<Icon src={ArrowDownTray} mini {size} class="shrink-0 text-fog" /> Download raw
		</a>
	{/if}
	<button type="button" class={item} onclick={copyId}>
		<Icon src={copied ? Check : ClipboardDocument} mini {size} class="shrink-0 text-fog" />
		{copied ? 'Copied' : 'Copy id'}
	</button>
	<a href={artistUrl} class={item}>
		<Icon src={User} mini {size} class="shrink-0 text-fog" /> Go to artist
	</a>
{/snippet}
