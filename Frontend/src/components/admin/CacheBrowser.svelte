<script module lang="ts">
	type Page<Row> = { rows: Row[]; total: number; offset: number; take: number; top: number | null };

	type Browse = {
		q: string;
		draft: string;
		filtered: boolean;
		offset: number;
		top: number | null;
		page: Page<unknown> | null;
		error: string;
	};

	/** Per `${target}/${table}`, kept across navigations like Oko's `browsers`: back to a table is back to the same page of it. */
	// eslint-disable-next-line svelte/prefer-svelte-reactivity -- a lookup of proxies that are themselves $state; never rendered
	const kept = new Map<string, Browse>();
</script>

<script lang="ts" generics="Row">
	import { untrack, type Snippet } from 'svelte';
	import { pager } from '$lib/admin';
	import { read } from '$requests/admin';
	import fleet from '$states/fleet.svelte';

	/**
	 * One paged table of a cache pod's database, read through Oko's unaudited `read` and acted on
	 * through its audited `action`. `top` is the pin the pod hands back with page 1, so rows added
	 * meanwhile do not shift the later pages; Search drops it, which is how new rows come into view.
	 * See CACHE_BROWSER_PLAN.md.
	 */
	let {
		target,
		table,
		columns,
		row,
		filter
	}: {
		target: string;
		table: string;
		columns: string[];
		row: Snippet<[Row]>;
		filter?: { name: string; label: string };
	} = $props();

	const PAGE_SIZE = 100;
	// the view is remounted for another target ({#key} on the service page), so these never change
	// svelte-ignore state_referenced_locally
	const key = `${target}/${table}`;
	let fresh = $state<Browse>({ q: '', draft: '', filtered: false, offset: 0, top: null, page: null, error: '' });
	const browse = kept.get(key) ?? fresh;
	kept.set(key, browse);

	let ticket = 0;
	let loading = $state(false);

	async function load(changes: Partial<Browse> = {}) {
		Object.assign(browse, changes);
		// a newer load (the next page clicked before this one landed) wins
		const mine = ++ticket;
		const params: Record<string, string | number> = { q: browse.q, offset: browse.offset, take: PAGE_SIZE };
		if (browse.top !== null) params.top = browse.top;
		if (browse.filtered && filter) params[filter.name] = 'true';

		loading = true;
		try {
			const page = await read<Page<Row>>(target, table, params);
			if (mine !== ticket) return;
			// a delete can empty the last page under you; show the one before it instead
			if (!page.rows.length && page.offset > 0)
				return load({ offset: pager(page.total, 0, page.take).last });
			Object.assign(browse, { page, error: '', top: page.top, offset: page.offset });
		} catch (failure) {
			if (mine === ticket) browse.error = failure instanceof Error ? failure.message : 'Could not read the table.';
		} finally {
			if (mine === ticket) loading = false;
		}
	}

	// once when opened, then after every action taken anywhere in the panel
	$effect(() => {
		void fleet.actions;
		untrack(() => load());
	});

	function search(event: SubmitEvent) {
		event.preventDefault();
		load({ q: browse.draft.trim(), offset: 0, top: null });
	}

	let at = $derived(browse.page && pager(browse.page.total, browse.page.offset, browse.page.take));
	let rows = $derived((browse.page?.rows ?? []) as Row[]);
</script>

<div class="mb-2.5 flex flex-wrap items-center gap-2.5">
	<form class="flex min-w-0 flex-1 basis-64 gap-2" onsubmit={search}>
		<input
			type="search"
			bind:value={browse.draft}
			placeholder="Search names, artists, albums or IDs…"
			aria-label="Search {table}"
			class="admin-field w-full max-w-md min-w-0 px-2.5"
		/>
		<button type="submit" class="admin-button" title="Search — again with the same words to take in rows added since"
			>Search</button
		>
	</form>
	{#if filter}
		<label class="flex items-center gap-1.5 text-xs whitespace-nowrap text-fog">
			<input
				type="checkbox"
				checked={browse.filtered}
				onchange={(event) => load({ filtered: event.currentTarget.checked, offset: 0, top: null })}
				class="rounded-[3px] border-haze bg-dark-0 text-primary-600"
			/>
			{filter.label}
		</label>
	{/if}
	{#if at && browse.page}
		{@const p = browse.page}
		<div class="flex items-center whitespace-nowrap" aria-busy={loading}>
			<button type="button" class="admin-button" title="First page"
				aria-label="First page" disabled={at.page <= 1} onclick={() => load({ offset: 0 })}
				>«</button
			>
			<button
				type="button"
				class="admin-button"
				title="Previous page"
				aria-label="Previous page"
				disabled={at.page <= 1}
				onclick={() => load({ offset: p.offset - p.take })}>‹</button
			>
			<span class="mx-1.5 text-xs text-fog"
				>Page {at.page} of {at.pages} · rows {at.from}–{at.to} of {p.total.toLocaleString()}</span
			>
			<button
				type="button"
				class="admin-button"
				title="Next page"
				aria-label="Next page"
				disabled={at.page >= at.pages}
				onclick={() => load({ offset: p.offset + p.take })}>›</button
			>
			<button
				type="button"
				class="admin-button"
				title="Last page"
				aria-label="Last page"
				disabled={at.page >= at.pages}
				onclick={() => load({ offset: at.last })}>»</button
			>
		</div>
	{/if}
</div>

<div class="admin-scroll">
	{#if browse.error}
		<p class="admin-empty text-ember!">{browse.error}</p>
	{:else if !browse.page}
		<p class="admin-empty">Loading…</p>
	{:else if !rows.length}
		<p class="admin-empty">Nothing here right now.</p>
	{:else}
		<table class="admin-table">
			<thead>
				<tr>{#each columns as column, index (index)}<th>{column}</th>{/each}</tr>
			</thead>
			<tbody>
				{#each rows as item, index (index)}
					<tr>{@render row(item)}</tr>
				{/each}
			</tbody>
		</table>
	{/if}
</div>
