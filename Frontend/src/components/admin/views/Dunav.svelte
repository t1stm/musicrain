<script lang="ts">
	import Action from '../Action.svelte';
	import Cards from '../Cards.svelte';
	import { ago, bytes } from '$lib/admin';
	import type { Snapshot } from '$requests/admin';

	type Entry = {
		key: string;
		label: string | null;
		bytes: number;
		state: string;
		contentType: string | null;
		expiresUtc: string | null;
	};
	type Dunav = { count: number; totalBytes: number; maxBytes: number; retentionMinutes: number; entries: Entry[] };

	let { name, snapshot }: { name: string; snapshot: Snapshot } = $props();
	let s = $derived(snapshot as unknown as Dunav);

	const evictAll =
		'Evict every cached entry?\n\nAnything still streaming finishes on its own handle; the next request re-fetches.';
	const pill: Record<string, string> = { complete: 'text-primary-500', downloading: 'text-gold' };
</script>

<Cards
	items={[
		{ value: s.count, label: 'entries' },
		{ value: bytes(s.totalBytes), label: `of ${bytes(s.maxBytes)}`, bar: s.maxBytes ? (s.totalBytes / s.maxBytes) * 100 : 0 },
		{ value: `${s.retentionMinutes}m`, label: 'retention' }
	]}
/>

<div class="mb-2.5">
	<Action
		target={name}
		action="evict-all"
		label="Evict everything"
		danger
		confirm={evictAll}
	/>
</div>

<div class="admin-scroll">
	{#if !s.entries.length}
		<p class="admin-empty">Nothing here right now.</p>
	{:else}
		<table class="admin-table">
			<thead><tr><th>What</th><th>Size</th><th>State</th><th>Type</th><th>Expires</th><th></th></tr></thead>
			<tbody>
				{#each s.entries as entry (entry.key)}
					<tr>
						<td class="wrap">{entry.label ?? '—'}</td>
						<td class="mono">{bytes(entry.bytes)}</td>
						<td><span class="rounded-full border border-haze px-2 text-xs {pill[entry.state] ?? 'text-fog'}">{entry.state}</span></td>
						<td class="mono">{entry.contentType ?? '—'}</td>
						<td>{entry.expiresUtc ? `in ${ago(entry.expiresUtc)}` : '—'}</td>
						<td><Action target={name} action="evict" params={{ key: entry.key }} label="Evict" /></td>
					</tr>
				{/each}
			</tbody>
		</table>
	{/if}
</div>
