<script lang="ts">
	import Cards from '../Cards.svelte';
	import Tracks from './Tracks.svelte';
	import { bytes } from '$lib/admin';
	import type { Snapshot } from '$requests/admin';

	type YouTube = { results?: number; downloads?: number; downloadBytes?: number };

	/** YouTube.db: every search result the pod has seen, and the webm cache indexed beside it. */
	let { name, snapshot }: { name: string; snapshot: Snapshot } = $props();
	let s = $derived(snapshot as unknown as YouTube);
</script>

<Cards
	items={[
		{ value: s.results ?? 0, label: 'cached results' },
		{ value: s.downloads ?? 0, label: 'downloaded' },
		{ value: bytes(s.downloadBytes), label: 'of webm' }
	]}
/>

<Tracks {name} />
