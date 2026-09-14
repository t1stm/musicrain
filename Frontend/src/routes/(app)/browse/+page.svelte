<script lang="ts">
	import { onMount } from 'svelte';
	import type { PageData } from './$types';
	import type { BrowseLevel } from '$requests/songs';
	import FolderRow from '$components/browse/FolderRow.svelte';
	import SearchRow from '$components/search/SearchRow.svelte';
	import RowSkeleton from '$components/RowSkeleton.svelte';

	const { data }: { data: PageData } = $props();

	let root = $state<BrowseLevel | null>(null);
	let loading = $state(true);

	onMount(async () => {
		root = await data.root;
		loading = false;
	});
</script>

<svelte:head><title>Browse · musicrain</title></svelte:head>

<div class="page page-column gap-6 p-4 sm:gap-9 sm:p-6 sm:pb-28">
	<div>
		<p class="eyebrow text-gold">The library</p>
		<h1
			class="mt-2 font-display text-lg font-light leading-tight tracking-tight text-chalk sm:text-2xl"
		>
			Browse by folder
		</h1>
		<p class="mt-2 max-w-lg text-fog">
			The music database in the shape it is stored in. Open a folder to see what is inside it.
		</p>
	</div>

	{#if loading}
		<div class="flex flex-col" aria-busy="true">
			{#each [...Array(8).keys()] as row (row)}
				<RowSkeleton />
			{/each}
		</div>
		<p class="sr-only" aria-live="polite">Opening the library.</p>
	{:else if !root}
		<p class="max-w-lg text-fog">The library could not be reached. Try again shortly.</p>
	{:else if root.folders.length === 0 && root.files.length === 0}
		<p class="max-w-lg text-fog">
			The library is empty. Once the server has indexed a folder of music it shows up here.
		</p>
	{:else}
		<section class="flex flex-col gap-2">
			<h2 class="eyebrow flex items-center gap-3 text-gold">
				{root.folders.length}
				{root.folders.length === 1 ? 'folder' : 'folders'}
				<span class="h-px flex-1 bg-gold/35"></span>
			</h2>
			<div class="flex flex-col">
				{#each root.folders as folder (folder.path)}
					<FolderRow {folder} />
				{/each}
			</div>
		</section>

		<!-- Tracks sitting at the top of the library rather than inside an artist folder. Rare,
		     and the reason the root is browsed exactly like every level below it. -->
		{#if root.files.length > 0}
			<section class="flex flex-col gap-2">
				<h2 class="eyebrow flex items-center gap-3">
					Not in a folder · {root.files.length}
					<span class="h-px flex-1 bg-haze"></span>
				</h2>
				<div class="flex flex-col">
					{#each root.files as file (file.id)}
						<SearchRow result={file} />
					{/each}
				</div>
			</section>
		{/if}
	{/if}
</div>
