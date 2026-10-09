<script lang="ts">
	import { ChevronRight, EllipsisHorizontal, Folder, FolderOpen, Icon, Play } from 'svelte-hero-icons';
	import Self from './FolderRow.svelte';
	import Lift from '$components/Lift.svelte';
	import SearchRow from '$components/search/SearchRow.svelte';
	import { getBrowse, getBrowseTracks, type BrowseFolder, type BrowseLevel } from '$requests/songs';
	import { closeOnBack } from '$lib/backWatcher.svelte';
	import { hold } from '$lib/press';
	import queue from '$states/queue.svelte';

	const { folder }: { folder: BrowseFolder } = $props();
	const contents = $props.id();

	let open = $state(false);
	let level = $state<BrowseLevel | null>(null);
	let loading = $state(false);
	let failed = $state(false);

	// Every open folder is a layer, so back walks back out of the tree one level at a
	// time — deepest first — and only leaves the page once the tree is closed.
	closeOnBack(
		() => open,
		() => (open = false)
	);

	/**
	 * One request per folder for the life of the page: a folder that has been opened keeps
	 * what it fetched, so closing and reopening costs nothing and the tree never flickers.
	 */
	async function load() {
		loading = true;
		failed = false;
		try {
			level = await getBrowse(folder.path, fetch);
		} catch {
			failed = true;
		} finally {
			loading = false;
		}
	}

	function toggle() {
		open = !open;
		if (open && !level && !loading) load();
	}

	// The folder's menu: a right-click, a held press or its "…", like a track's.
	let menuOpen = $state(false);
	let starting = $state(false);
	let playFailed = $state(false);

	$effect(() => {
		if (!menuOpen) playFailed = false;
	});

	/**
	 * Everything beneath the folder becomes the queue. The menu stays up until the tracks are
	 * in: closed at once, a big folder's wait would read as nothing happening, and a failure
	 * would have nowhere to say so. Closed while they load is a change of mind.
	 */
	async function playAll() {
		starting = true;
		playFailed = false;
		try {
			const tracks = await getBrowseTracks(folder.path, fetch);
			if (!menuOpen) return;
			queue.replaceWith(tracks.map((track) => ({ ...track, origin: { kind: 'browse', id: folder.path } })));
			menuOpen = false;
		} catch {
			playFailed = true;
		} finally {
			starting = false;
		}
	}
</script>

<div>
	<!-- The row the menu lifts out, and the only part of the folder that opens it: what the
	     folder holds sits below, outside it, so a right-click or a hold on a row inside never
	     reaches this one's menu. The menu key arrives as a `contextmenu` on the focused button.
	     A held press is the menu's alone: no callout, no selection. -->
	<div
		data-preview
		class="group flex select-none items-center gap-3 rounded-row pr-2 transition-colors [-webkit-touch-callout:none] not-has-open:hover:bg-surface-100 sm:gap-3.5 sm:pr-2.5"
		{@attach hold(() => (menuOpen = true))}
	>
		<!-- A real button, so Enter, Space and the focus ring all arrive for free — SearchRow
		     needs its role/keydown pair only because it wraps links of its own. -->
		<button
			type="button"
			aria-expanded={open}
			aria-controls={contents}
			onclick={toggle}
			class="grid min-w-0 flex-1 grid-cols-[2.75rem_minmax(0,1fr)_auto] items-center gap-3 rounded-row py-2 pl-2 text-left transition-colors active:bg-surface-200 focus-visible:bg-surface-100 focus-visible:outline-2 focus-visible:outline-primary-200 sm:gap-3.5 sm:pl-2.5"
		>
			<!-- The artwork slot, to the pixel: a folder is a cover you have not opened yet, so
			     folders and tracks share one column and the list reads as one list. -->
			<span
				class="flex size-11 items-center justify-center rounded-art border transition-colors {open
					? 'border-gold/45 bg-gold/10 text-gold'
					: 'border-haze bg-surface-0 text-fog group-hover:text-gold'}"
			>
				<Icon src={open ? FolderOpen : Folder} mini size="20" />
			</span>

			<span class="min-w-0">
				<span class="line-clamp-2 text-sm font-medium leading-snug text-chalk">{folder.name}</span>
			</span>

			<span class="flex items-center gap-2.5">
				<span class="font-mono text-[0.79rem] text-fog"
					>{folder.songs}<span class="sr-only"> {folder.songs === 1 ? 'track' : 'tracks'}</span></span
				>
				<Icon
					src={ChevronRight}
					mini
					size="16"
					class="shrink-0 text-fog transition-transform duration-150 {open ? 'rotate-90' : ''}"
				/>
			</span>
		</button>

		<!-- A <details>, as TrackMenu's "…" is: the lift hides one in its copy of the row. -->
		<details
			bind:open={menuOpen}
			class="sm:pointer-fine:opacity-0 sm:pointer-fine:transition-opacity sm:pointer-fine:group-hover:opacity-100 sm:pointer-fine:group-focus-within:opacity-100 sm:pointer-fine:open:opacity-100"
		>
			<summary
				aria-label="More actions for {folder.name}"
				class="flex size-11 list-none items-center justify-center rounded-[5px] border border-haze text-fog hover:bg-surface-200 hover:text-chalk focus-visible:outline-2 focus-visible:outline-primary-200 sm:size-7 [&::-webkit-details-marker]:hidden"
			>
				<Icon src={EllipsisHorizontal} mini size="16" />
			</summary>
		</details>

		<Lift bind:open={menuOpen} label={folder.name}>
			{#snippet children(item, size, list)}
				<div class="grid {list}">
					<button type="button" class={item} disabled={starting} onclick={playAll}>
						<Icon src={Play} mini {size} class="shrink-0 text-primary-500" />
						{starting ? 'Starting…' : 'Play All'}
					</button>
					{#if playFailed}
						<p class="px-4 py-3 text-ember" role="alert">Could not open this folder.</p>
					{/if}
				</div>
			{/snippet}
		</Lift>
	</div>

	{#if open}
		<!-- The thread. It drops from the centre of the open folder's tile and runs the height
		     of its contents, so every folder you are inside is joined to what it holds — the
		     nested golds are the trail, which is why this page has no breadcrumb. -->
		<div id={contents} class="ml-[1.875rem] border-l border-gold/25 pl-1 sm:ml-8 sm:pl-2">
			{#if loading}
				<p class="px-2 py-2 text-sm text-fog">Opening…</p>
			{:else if failed}
				<p class="flex flex-wrap items-center gap-2 px-2 py-2 text-sm text-fog">
					Could not open this folder.
					<button
						type="button"
						class="rounded-[5px] border border-haze px-2 py-1 text-xs font-semibold text-chalk hover:bg-surface-200 focus-visible:outline-2 focus-visible:outline-primary-200"
						onclick={load}>Try again</button
					>
				</p>
			{:else if level}
				{#each level.folders as child (child.path)}
					<Self folder={child} />
				{/each}
				{#each level.files as file (file.id)}
					<SearchRow result={file} origin={{ kind: 'browse', id: folder.path }} />
				{/each}
				{#if level.folders.length === 0 && level.files.length === 0}
					<p class="px-2 py-2 text-sm text-fog">This folder has no tracks.</p>
				{/if}
			{/if}
		</div>
	{/if}
</div>
