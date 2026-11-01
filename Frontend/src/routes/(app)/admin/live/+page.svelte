<script lang="ts">
	import Feed from '$components/admin/Feed.svelte';
	import fleet from '$states/fleet.svelte';
</script>

<svelte:head><title>Live activity · Admin · musicrain</title></svelte:head>

<h1 class="font-display text-lg leading-tight font-light tracking-tight text-chalk sm:text-2xl">Live activity</h1>
<p class="mt-2 mb-4 text-sm text-fog">
	Every request the watched services report, as it happens — newest first, the last 500.
	{#if !fleet.following}<span class="text-gold">Reconnecting…</span>{/if}
</p>

<div class="admin-scroll py-1.5" aria-live="off">
	<Feed />
</div>
