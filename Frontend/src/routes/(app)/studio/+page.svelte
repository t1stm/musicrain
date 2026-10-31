<script lang="ts">
	// A build folds import.meta.env.DEV to false, so the import below -- and the whole studio with it --
	// never reaches a release bundle.
	const studio = import.meta.env.DEV ? import('$components/studio/Studio.svelte') : null;
</script>

{#if studio}
	{#await studio then module}
		<module.default />
	{/await}
{/if}
