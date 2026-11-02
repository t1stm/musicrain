<script lang="ts">
	import Skeleton from '$components/Skeleton.svelte';

	// SearchRow's `number` column, for a list whose rows will wear one
	const { numbered = false }: { numbered?: boolean } = $props();
</script>

<!-- SearchRow's and FolderRow's shared grid, to the pixel: one list, whether the
     rows in it have arrived yet or not. -->
<div
	class="grid {numbered
		? 'grid-cols-[1.5rem_2.75rem_minmax(0,1fr)_auto]'
		: 'grid-cols-[2.75rem_minmax(0,1fr)_auto]'} items-center gap-3 rounded-row px-2 py-2 sm:gap-3.5 sm:px-2.5"
>
	{#if numbered}<Skeleton class="ml-auto h-3 w-3" />{/if}
	<Skeleton class="size-11" />
	<div class="grid min-w-0 gap-1.5 py-0.5">
		<Skeleton class="h-3 w-3/5 max-w-72" />
		<Skeleton class="h-3 w-2/5 max-w-48" />
	</div>
	<Skeleton class="hidden h-3 w-14 sm:block" />
</div>
