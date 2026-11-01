<script lang="ts">
	import { dec, statusTone } from '$lib/admin';
	import fleet from '$states/fleet.svelte';

	/** The merged live feed: every line on the Live page, or the newest few in the strip. */
	let { limit }: { limit?: number } = $props();

	let lines = $derived(limit ? fleet.feed.slice(0, limit) : fleet.feed);
</script>

{#each lines as line (line.id)}
	<p class="truncate px-3 py-0.5 font-mono text-xs text-chalk">
		<span class="inline-block min-w-28 text-fog">{line.target}</span><span class={statusTone(line.status)}
			>{line.status}</span
		>
		{line.method}
		{dec(line.path)} <span class="text-fog">{line.ms}ms</span>
	</p>
{:else}
	<p class="px-3 py-0.5 font-mono text-xs text-fog">{fleet.following ? 'Waiting for requests…' : 'Connecting…'}</p>
{/each}
