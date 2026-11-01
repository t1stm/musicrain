<script lang="ts">
	import Action from '../Action.svelte';
	import CacheBrowser from '../CacheBrowser.svelte';
	import Cards from '../Cards.svelte';
	import { clock } from '$lib/admin';
	import type { Snapshot } from '$requests/admin';

	type Spotify = { results?: number; searchLimit: number };
	type Result = { id: string; name: string | null; artist: string | null; album: string | null; duration: string | null };

	/** Spotify hands over names only, so its cache is the whole of its state. */
	let { name, snapshot }: { name: string; snapshot: Snapshot } = $props();
	let s = $derived(snapshot as unknown as Spotify);
</script>

<Cards
	items={[
		{ value: s.results ?? 0, label: 'remembered tracks' },
		{ value: s.searchLimit, label: 'hits per search' }
	]}
/>

<h3 class="eyebrow mb-2">Search cache</h3>
<CacheBrowser target={name} table="results" columns={['Track', 'Artist', 'Album', 'Length', 'ID', '']}>
	{#snippet row(result: Result)}
		<td class="wrap">{result.name ?? '—'}</td>
		<td class="wrap">{result.artist ?? '—'}</td>
		<td class="wrap">{result.album ?? '—'}</td>
		<td class="mono">{clock(result.duration)}</td>
		<td class="mono">{result.id}</td>
		<td>
			<Action
				target={name}
				action="forget"
				params={{ id: result.id }}
				label="Forget"
				danger
				confirm={`Forget "${result.name}" from the search cache?\n\nThe next search that lists it adds it back.`}
			/>
		</td>
	{/snippet}
</CacheBrowser>
