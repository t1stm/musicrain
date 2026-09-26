<script lang="ts">
	import { onMount } from 'svelte';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';

	// The list is this page on a phone. Wider, the list is a rail beside a category, and a
	// rail with nothing beside it is half a page of nothing — so open the first one in place.
	onMount(() => {
		if (matchMedia('(min-width: 40rem)').matches)
			goto(resolve('/settings/account'), { replaceState: true });
	});
</script>

<svelte:head><title>Settings · musicrain</title></svelte:head>
