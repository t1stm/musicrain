<script lang="ts">
	import Action from '../Action.svelte';
	import Cards from '../Cards.svelte';
	import Tracks from './Tracks.svelte';
	import { bytes } from '$lib/admin';
	import type { Snapshot } from '$requests/admin';

	type Deezer = { results?: number; count: number; totalBytes: number; maxBytes: number; canDownload: boolean };

	/** Deezer's cache.db: every track a search listed or a play downloaded, in one table. */
	let { name, snapshot }: { name: string; snapshot: Snapshot } = $props();
	let s = $derived(snapshot as unknown as Deezer);
</script>

<Cards
	items={[
		{ value: s.results ?? 0, label: 'tracks' },
		{ value: s.count, label: 'downloaded' },
		{ value: bytes(s.totalBytes), label: `of ${bytes(s.maxBytes)}`, bar: s.maxBytes ? (s.totalBytes / s.maxBytes) * 100 : 0 },
		{ value: s.canDownload ? 'yes' : 'no', label: 'can download' }
	]}
/>

{#if !s.canDownload}
	<p class="mb-4 max-w-2xl rounded-panel border border-ember/50 p-3 text-[0.8125rem] leading-normal text-ember">
		No DEEZER_ARL — this pod is metadata only. Deezer results still show up, but Gaida resolves them against the
		library or YouTube, and nothing here can be promoted.
	</p>
{/if}

<div class="mb-2.5">
	<Action
		target={name}
		action="evict-all"
		label="Evict every download"
		danger
		confirm={`Delete all ${s.count} downloaded track(s)?\n\nEach one is downloaded again the next time it is played, and stays listed here meanwhile.`}
	/>
</div>

<Tracks {name} />
