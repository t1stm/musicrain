<script lang="ts">
	import { untrack } from 'svelte';
	import { resolve } from '$app/paths';
	import { nearEnd } from '$lib/paging';
	import { listHistory, resolveIds, type Play } from '$requests/history';
	import { AudioApiError } from '$requests/songs';
	import account from '$states/account.svelte';
	import type { SearchResult } from '$states/search.svelte';
	import SearchRow from '$components/search/SearchRow.svelte';
	import RowSkeleton from '$components/RowSkeleton.svelte';

	type Row = { play: Play; track: SearchResult };

	let rows = $state<Row[]>([]);
	let next = $state<string | null>(null);
	let done = $state(false);
	let loading = $state(false);
	let failed = $state(false);

	// Bumped on every account change, so a page that was still loading for the last one is
	// dropped rather than appended to the next one's list.
	let generation = 0;

	async function more() {
		if (loading || done) return;
		const asked = generation;
		loading = true;
		failed = false;
		try {
			const page = await listHistory(account.token, next);
			const tracks = await resolveIds(page.plays.map((play) => play.trackId));
			if (asked !== generation) return;
			// a play whose track no longer resolves — deleted, taken down — is not shown
			rows.push(...page.plays.flatMap((play) => {
				const track = tracks.get(play.trackId);
				return track ? [{ play, track }] : [];
			}));
			next = page.next;
			done = page.next === null;
		} catch (error) {
			if (asked !== generation) return;
			if (error instanceof AudioApiError) account.reject(error.status);
			failed = true;
		} finally {
			if (asked === generation) loading = false;
		}
	}

	// An effect, not onMount: the layout reads the stored session after this page mounts, and
	// signing in or out elsewhere changes whose history this is.
	$effect(() => {
		void account.token;
		untrack(() => {
			generation++;
			rows = [];
			next = null;
			done = false;
			loading = false;
			void more();
		});
	});

	const dayOf = (iso: string) => new Date(iso).toDateString();

	function dayLabel(iso: string) {
		const date = new Date(iso);
		const today = new Date();
		const yesterday = new Date(today.getFullYear(), today.getMonth(), today.getDate() - 1);

		if (date.toDateString() === today.toDateString()) return 'Today';
		if (date.toDateString() === yesterday.toDateString()) return 'Yesterday';
		return date.toLocaleDateString(undefined, {
			weekday: 'long',
			day: 'numeric',
			month: 'long',
			...(date.getFullYear() === today.getFullYear() ? {} : { year: 'numeric' })
		});
	}

	const timeOf = (iso: string) =>
		new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });

	// grouped by the listener's own calendar day, which is what "yesterday" means to them
	let days = $derived(
		rows.reduce<{ key: string; label: string; rows: Row[] }[]>((groups, row) => {
			const key = dayOf(row.play.startedUtc);
			const last = groups.at(-1);
			if (last?.key === key) last.rows.push(row);
			else groups.push({ key, label: dayLabel(row.play.startedUtc), rows: [row] });
			return groups;
		}, [])
	);
</script>

<svelte:head><title>History · musicrain</title></svelte:head>

<div class="page page-column gap-8 px-2 py-6 sm:px-8 sm:pb-28">
	<header>
		<p class="eyebrow text-primary-500">Listening history</p>
		<h1 class="mt-2 font-display text-xl font-light leading-tight tracking-tight text-chalk sm:text-3xl">
			What you played
		</h1>
		<p class="mt-2 text-sm text-fog">
			{account.signedIn ? 'On every device signed in to your account.' : 'On this device.'}
			<a
				href={resolve('/settings/playback')}
				class="text-primary-500 underline-offset-4 hover:underline">Pause or clear it</a
			>
		</p>
	</header>

	{#each days as day (day.key)}
		<section class="flex flex-col gap-2">
			<h2 class="eyebrow flex items-center gap-3">
				{day.label}
				<span class="h-px flex-1 bg-haze"></span>
			</h2>
			<div class="flex flex-col">
				{#each day.rows as row (row.play.id)}
					<div class="flex items-center gap-1 sm:gap-2">
						<time
							datetime={row.play.startedUtc}
							class="w-11 shrink-0 text-right font-mono text-[0.68rem] text-fog">{timeOf(row.play.startedUtc)}</time
						>
						<div class="min-w-0 flex-1">
							<SearchRow result={row.track} origin={{ kind: 'history' }} />
						</div>
					</div>
				{/each}
			</div>
		</section>
	{/each}

	{#if loading}
		<div class="flex flex-col" aria-busy="true">
			{#each [0, 1, 2, 3, 4] as slot (slot)}<RowSkeleton />{/each}
		</div>
	{:else if failed}
		<p class="text-sm text-fog">
			Could not reach the history service.
			<button type="button" class="font-semibold text-primary-500 hover:underline" onclick={more}>Try again</button>
		</p>
	{:else if done && rows.length === 0}
		<p class="text-sm text-fog">Nothing yet. Whatever you play from now on shows up here.</p>
	{:else if !done}
		<div {@attach nearEnd(more)} aria-hidden="true"></div>
	{/if}
</div>
