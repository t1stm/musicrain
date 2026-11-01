<script lang="ts">
	import { untrack } from 'svelte';
	import { ago, statusTone } from '$lib/admin';
	import { audit, type AuditEntry } from '$requests/admin';
	import fleet from '$states/fleet.svelte';

	let rows = $state<AuditEntry[] | null>(null);
	let error = $state('');

	async function load() {
		error = '';
		try {
			rows = await audit();
		} catch (failure) {
			error = failure instanceof Error ? failure.message : 'Could not read the audit log.';
		}
	}

	// when opened, and after every action taken from the panel
	$effect(() => {
		void fleet.actions;
		untrack(load);
	});
</script>

<svelte:head><title>Audit · Admin · musicrain</title></svelte:head>

<h1 class="font-display text-lg leading-tight font-light tracking-tight text-chalk sm:text-2xl">Audit</h1>
<p class="mt-2 mb-4 text-sm text-fog">
	Everything changed through the panel since Oko started, refusals included — in memory, newest first, capped at
	1000.
	<button type="button" class="admin-button ml-1" onclick={load}>Refresh</button>
</p>

<div class="admin-scroll">
	{#if error}
		<p class="admin-empty text-ember!">{error}</p>
	{:else if !rows}
		<p class="admin-empty">Loading…</p>
	{:else if !rows.length}
		<p class="admin-empty">Nothing changed yet.</p>
	{:else}
		<table class="admin-table">
			<thead><tr><th>When</th><th>Who</th><th>Service</th><th>Action</th><th>Parameters</th><th>Result</th></tr></thead>
			<tbody>
				{#each rows as entry, index (index)}
					<tr>
						<td>{ago(entry.at)} ago</td>
						<td>{entry.who}</td>
						<td>{entry.target}</td>
						<td class="mono">{entry.action}</td>
						<td class="mono wrap">{entry.parameters}</td>
						<td class="mono wrap {statusTone(entry.status)}">{entry.status}{entry.error ? ` ${entry.error.slice(0, 80)}` : ''}</td>
					</tr>
				{/each}
			</tbody>
		</table>
	{/if}
</div>
