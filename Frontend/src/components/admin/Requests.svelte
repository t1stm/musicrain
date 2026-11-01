<script lang="ts">
	import { ago, dec, statusTone } from '$lib/admin';
	import { requests, type RequestRow } from '$requests/admin';

	/** One target's request ring, newest first. Fetched when opened and on Refresh, never from the poll. */
	let { target }: { target: string } = $props();

	let rows = $state<RequestRow[] | null>(null);
	let error = $state('');

	async function load() {
		error = '';
		try {
			rows = (await requests(target)).slice().reverse();
		} catch (failure) {
			error = failure instanceof Error ? failure.message : 'Could not read the requests.';
		}
	}

	$effect(() => {
		load();
	});
</script>

<div class="mb-2.5 flex items-center gap-3">
	<button type="button" class="admin-button" onclick={load}>Refresh</button>
	{#if rows}<span class="text-xs text-fog">{rows.length} recent requests</span>{/if}
</div>

<div class="admin-scroll">
	{#if error}
		<p class="admin-empty text-ember!">{error}</p>
	{:else if !rows}
		<p class="admin-empty">Loading…</p>
	{:else if !rows.length}
		<p class="admin-empty">Nothing here right now.</p>
	{:else}
		<table class="admin-table">
			<thead><tr><th>When</th><th>Method</th><th>Path</th><th>Status</th><th>Time</th></tr></thead>
			<tbody>
				{#each rows as row, index (index)}
					<tr>
						<td>{ago(row.at)} ago</td>
						<td class="mono">{row.method}</td>
						<td class="mono wrap">{dec(row.path)}</td>
						<td class="mono {statusTone(row.status)}">{row.status}</td>
						<td class="mono">{row.ms} ms</td>
					</tr>
				{/each}
			</tbody>
		</table>
	{/if}
</div>
