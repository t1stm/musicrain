<script lang="ts">
	import {
		ArrowDownTray,
		ArrowsRightLeft,
		Check,
		ChevronLeft,
		ChevronRight,
		ClipboardDocument,
		EllipsisHorizontal,
		Icon,
		Plus,
		QueueList,
		User
	} from 'svelte-hero-icons';
	import { tick, untrack } from 'svelte';
	import { cubicOut } from 'svelte/easing';
	import { MediaQuery } from 'svelte/reactivity';
	import { fade, scale } from 'svelte/transition';
	import { resolve } from '$app/paths';
	import { heroArtist } from '$lib';
	import { closeOnBack } from '$lib/backWatcher.svelte';
	import { dismiss } from '$lib/dismiss';
	import PlaylistCover from '$components/playlist/PlaylistCover.svelte';
	import ReplacePicker from '$components/playlist/ReplacePicker.svelte';
	import type { PlaylistSummary } from '$requests/playlists';
	import account from '$states/account.svelte';
	import advanced from '$states/advanced.svelte';
	import playlists, { toSnapshot } from '$states/playlists.svelte';
	import type { SearchResult } from '$states/search.svelte';
	import queue from '$states/queue.svelte';

	// A track's menu. Bindable so a swipe on a row or a held press on a card can open it
	// without the "…". `trigger={false}` leaves the "…" out for a caller that opens it itself.
	// `replace`, when given, is one more action: a search under the lifted track, and the
	// pick handed to the caller.
	//
	// Wider than a phone it drops from the "…". On a phone, and anywhere without a "…" to
	// drop from, it lifts the track out instead: a copy of the `data-preview` around the menu
	// — the row or the card, exactly as drawn — carried from its place to the middle of the
	// screen, the actions under it, and carried back into the gap it left when it closes.
	let {
		result,
		open = $bindable(false),
		trigger = true,
		replace,
		class: className = ''
	}: {
		result: SearchResult;
		open?: boolean;
		trigger?: boolean;
		replace?: (result: SearchResult) => void;
		class?: string;
	} = $props();

	const phone = new MediaQuery('width < 40rem');
	const still = new MediaQuery('prefers-reduced-motion: reduce');
	// Where the menu drops from the "…" rather than lifting the track. Replacing lifts on any
	// screen: a search and its answers need more room than a dropdown.
	let dropdown = $derived(trigger && !phone.current);
	let replacing = $state(false);
	let lifted = $derived(open && (!dropdown || replacing));

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

	// "Add to playlist" swaps the actions for your playlists in the same box. The dropdown and
	// the lift both draw `actions`, so neither grows a second menu of its own.
	let picking = $state(false);
	let naming = $state(false);
	let draftName = $state('');
	/** The answer on the playlist that was pressed, held until the menu takes itself away. */
	let landed = $state<{ id: string; name: string; added: boolean } | null>(null);

	$effect(() => {
		if (open) return;
		picking = false;
		naming = false;
		replacing = false;
	});

	// Your playlists, fetched as the menu opens rather than when "Add to playlist" is pressed:
	// a step captures the list at the size it has then, and a list that arrives under a running
	// step is squeezed into that size until the step ends. Once per page — `pick` refreshes.
	$effect(() => {
		if (open && account.token && untrack(() => playlists.mine.length === 0)) playlists.loadMine();
	});

	/**
	 * Steps between the actions and a list of their own — your playlists, or a replacement.
	 * The frame grows or shrinks to the new list while the rows slide the way the step goes:
	 * in from the right going deeper, back from the left coming out (app.css). The same
	 * morph as the player's; without view transitions, or with motion reduced, a plain swap.
	 */
	function step(forward: boolean, update: () => void) {
		if (still.current || !document.startViewTransition) return update();
		const root = document.documentElement;
		root.dataset.menuStep = forward ? 'in' : 'out';
		document
			.startViewTransition(async () => {
				update();
				await tick();
			})
			.finished.finally(() => delete root.dataset.menuStep);
	}

	// From a dropdown, replacing is a lift, which has its own motion; in a lift it is a step.
	const replaceStep = (forward: boolean) =>
		dropdown ? (replacing = forward) : step(forward, () => (replacing = forward));

	async function pick() {
		// fresh counts; and a first load still on its way is waited for, for the reason above
		const loaded = playlists.loadMine();
		if (playlists.mine.length === 0) await loaded;
		step(true, () => (picking = true));
	}

	// the same exit as a copy: the answer stays long enough to read, then the menu goes
	function answer(playlist: { id: string; name: string }, added: boolean) {
		landed = { id: playlist.id, name: playlist.name, added };
		clearTimeout(settle);
		settle = setTimeout(() => {
			landed = null;
			open = false;
		}, 1100);
	}

	async function addTo(playlist: PlaylistSummary) {
		const added = await playlists.add(playlist.id, result);
		if (added !== null) answer(playlist, added);
	}

	async function create(event: SubmitEvent) {
		event.preventDefault();
		const name = draftName.trim();
		if (!name) return;

		const made = await playlists.save({ name, tracks: [toSnapshot(result)] });
		if (!made) return;
		naming = false;
		draftName = '';
		answer(made, true);
	}

	// the control that was pressed is gone once the view swaps, so the focus goes somewhere real
	const focus = (node: HTMLElement) => node.focus();

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
				class="absolute right-0 z-20 mt-1 grid rounded-panel border border-haze bg-surface-100 p-1 text-left text-xs {picking
					? 'w-60'
					: 'w-44'}"
				style:view-transition-name={open ? 'track-menu' : undefined}
			>
				{@render actions(
					'flex min-h-10 w-full items-center gap-2 rounded-art px-2 text-left hover:bg-surface-200',
					'14',
					'gap-0.5'
				)}
			</div>
		{/if}
	</details>
{/if}

<!-- Escape and Android's back ask the dialog to close before they reach the back stack, so
     the dialog says yes through `open` rather than closing itself under the lift's motion.
     A long press that opened it may still be down; it selects nothing and opens no menu —
     except in a field, which needs its menu to paste. -->
{#if lifted}
	<dialog
		{@attach lift}
		aria-label={result.name}
		class="m-0 size-full max-h-none max-w-none select-none overflow-y-auto overscroll-contain bg-transparent p-0 text-chalk backdrop:bg-transparent"
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
		     page, and the shade went with it until the step was over. -->
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
				     fading it from one place to the other -->
				<div
					data-lift
					class="max-w-full rounded-panel border border-haze bg-surface-100 p-1.5 [&>*]:max-w-full"
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
					{@render actions(
						'flex min-h-13 w-full items-center gap-3.5 px-4 text-left outline-none hover:bg-surface-200 focus-visible:bg-surface-200 active:transform-none active:bg-surface-200',
						'18',
						'divide-y divide-haze'
					)}
				</div>
			</div>
		</div>
	</dialog>
{/if}

<!-- One list, two sizes. Play next wears the primary colour of its own swipe key. The rows
     are a box of their own, apart from the frame, so a step can slide them inside it. -->
{#snippet actions(item: string, size: string, list: string)}
	<div class="grid {list}" style:view-transition-name={open ? 'track-menu-rows' : undefined}>
		{@render view(item, size, list)}
	</div>
{/snippet}

{#snippet view(item: string, size: string, list: string)}
	{#if picking}
		{@render playlistPicker(item, size, list)}
	{:else if replacing && replace}
		<ReplacePicker
			track={result}
			{item}
			{size}
			{list}
			back={() => replaceStep(false)}
			pick={(chosen) => {
				open = false;
				replace(chosen);
			}}
		/>
	{:else}
		{@render trackActions(item, size)}
	{/if}
{/snippet}

{#snippet trackActions(item: string, size: string)}
	<button type="button" class={item} onclick={playNext}>
		<Icon src={QueueList} mini {size} class="shrink-0 text-primary-500" /> Play Next
	</button>
	<!-- a playlist is an account's, so signed out there is nothing to add to -->
	{#if account.token}
		<button type="button" class={item} onclick={pick}>
			<Icon src={Plus} mini {size} class="shrink-0 text-fog" /> Add to Playlist
			<Icon src={ChevronRight} mini {size} class="ml-auto shrink-0 text-fog" />
		</button>
	{/if}
	{#if replace}
		<button type="button" class={item} onclick={() => replaceStep(true)}>
			<Icon src={ArrowsRightLeft} mini {size} class="shrink-0 text-fog" /> Replace…
		</button>
	{/if}
	<!-- off unless Settings → Advanced turns them on -->
	{#if advanced.trackTools}
		<!-- room queue items carry no contentUrl; hide the action rather than
		     linking nowhere -->
		{#if result.contentUrl}
			<a href={result.contentUrl} download class={item} onclick={close}>
				<Icon src={ArrowDownTray} mini {size} class="shrink-0 text-fog" /> Download Raw
			</a>
		{/if}
		<button type="button" class={item} onclick={copyId}>
			<Icon src={copied ? Check : ClipboardDocument} mini {size} class="shrink-0 text-fog" />
			{copied ? 'Copied' : 'Copy ID'}
		</button>
	{/if}
	<!-- closes as it goes: from one artist's page to another the row, and so the menu, stays -->
	<a href={artistUrl} class={item} onclick={close}>
		<Icon src={User} mini {size} class="shrink-0 text-fog" /> Go to Artist
	</a>
{/snippet}

<!-- Every row leads with a sleeve, the new one an empty dashed one, so the names line up. -->
{#snippet playlistPicker(item: string, size: string, list: string)}
	<button
		type="button"
		class={item}
		onclick={() => step(false, () => (picking = false))}
		{@attach focus}
	>
		<Icon src={ChevronLeft} mini {size} class="shrink-0 text-fog" /> Add to Playlist
	</button>
	<!-- scrolls on its own, so a long list keeps the way back where it was; the names truncate
	     to the box rather than widen it past the lifted track -->
	<div
		class="grid max-h-[min(20rem,50dvh)] grid-cols-1 overflow-y-auto overscroll-contain contain-inline-size {list}"
	>
		{#if naming}
			<form class={item} onsubmit={create}>
				<!-- select-text: iOS will not type into a field under the lift's select-none -->
				<input
					type="text"
					bind:value={draftName}
					maxlength="80"
					placeholder="Playlist name"
					aria-label="New playlist name"
					class="min-w-0 flex-1 select-text rounded-art border border-haze bg-dark-0 px-2 py-1 text-chalk placeholder:text-fog focus:border-primary-0 focus:ring-primary-0"
					{@attach focus}
				/>
				<button
					type="submit"
					class="shrink-0 rounded-art bg-primary-600 px-2.5 py-1 font-semibold text-white hover:bg-primary-0 disabled:opacity-60"
					disabled={!draftName.trim() || playlists.loading}
				>
					Create
				</button>
			</form>
		{:else}
			<button type="button" class={item} onclick={() => (naming = true)}>
				<span
					class="grid size-8 shrink-0 place-items-center rounded-art border border-dashed border-surface-400 text-fog"
				>
					<Icon src={Plus} mini size="14" />
				</span>
				New Playlist
			</button>
		{/if}
		{#each playlists.mine as playlist (playlist.id)}
			<button
				type="button"
				class={item}
				disabled={playlists.loading || landed !== null}
				onclick={() => addTo(playlist)}
			>
				<PlaylistCover {playlist} class="size-8 shrink-0 rounded-art object-cover" />
				<span class="min-w-0 flex-1 truncate">{playlist.name}</span>
				{#if landed?.id === playlist.id}
					{#if landed.added}
						<span class="flex shrink-0 items-center gap-1.5 text-primary-500">
							<span
								class="grid size-5 animate-ripple place-items-center rounded-full bg-primary-600 text-white motion-reduce:animate-none"
							>
								<Icon src={Check} mini size="12" />
							</span>
							Added
						</span>
					{:else}
						<span class="shrink-0 text-fog">Already in</span>
					{/if}
				{:else}
					<span class="shrink-0 font-mono text-[0.68rem] text-fog">{playlist.trackCount}</span>
				{/if}
			</button>
		{:else}
			{#if playlists.loading}
				<p class="px-2 py-2 text-fog">Loading your playlists…</p>
			{/if}
		{/each}
	</div>
	{#if playlists.error}
		<p class="px-2 py-2 text-ember" role="alert">{playlists.error}</p>
	{/if}
	<p class="sr-only" aria-live="polite">
		{landed ? `${landed.added ? 'Added to' : 'Already in'} ${landed.name}` : ''}
	</p>
{/snippet}
