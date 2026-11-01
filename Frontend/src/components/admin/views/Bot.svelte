<script lang="ts">
	import Cards from '../Cards.svelte';
	import { ago } from '$lib/admin';
	import type { Snapshot } from '$requests/admin';
	import fleet from '$states/fleet.svelte';

	type Status = { presence: string; activity: string | null; text: string | null; url: string | null };
	type Account = { name: string; username: string; master: boolean; guilds: number; playing: number; status: Status | null };
	type Player = {
		account: string;
		guild: string | null;
		channel: string | null;
		listeners: number;
		track: string | null;
		requester: string | null;
		queueLength: number;
		currentIndex: number;
		paused: boolean;
		elapsed: string;
		length: string | null;
		loop: string;
	};
	type Event = {
		at: string;
		kind: string;
		account: string;
		guild: string | null;
		channel: string | null;
		user: string | null;
		detail: string;
	};
	type Bot = { accounts?: Account[]; players?: Player[]; events?: Event[]; api?: string };

	/**
	 * The Discord bot. It serves no traffic of its own, so its Requests tab stays empty and its audit
	 * trail comes down in the snapshot instead. The one thing changed from here is each account's
	 * Discord status: nothing else acts on a guild somebody is listening in.
	 */
	let { name, snapshot }: { name: string; snapshot: Snapshot } = $props();
	let s = $derived(snapshot as unknown as Bot);
	let accounts = $derived(s.accounts ?? []);
	let players = $derived(s.players ?? []);

	const PRESENCES = ['Online', 'Idle', 'DoNotDisturb', 'Invisible'];
	// Custom is missing on purpose: Discord reads a custom status from `state`, which DSharpPlus
	// cannot set — the bot refuses it too, with that as the reason
	const ACTIVITIES = ['none', 'Playing', 'ListeningTo', 'Watching', 'Competing', 'Streaming'];

	type Draft = { presence: string; activity: string; text: string; url: string };
	/** What each account's controls show, by the configured name set-status takes. */
	let drafts = $state<Record<string, Draft>>({});
	let busy = $state<string | null>(null);
	let errors = $state<Record<string, string>>({});

	// seeded from what each account shows now, once; after that the controls own it
	$effect.pre(() => {
		for (const account of accounts)
			drafts[account.name] ??= {
				presence: account.status?.presence ?? 'Online',
				activity: account.status?.activity ?? 'none',
				text: account.status?.text ?? '',
				url: account.status?.url ?? ''
			};
	});

	const statusText = (status: Status | null) =>
		!status ? '—' : status.activity ? `${status.presence}, ${status.activity} ${status.text ?? ''}` : status.presence;

	async function save(account: Account, clear: boolean) {
		const draft = drafts[account.name];
		const params: Record<string, string> = { account: account.name };
		if (!clear) {
			params.presence = draft.presence;
			params.activity = draft.activity;
			if (draft.activity !== 'none') params.text = draft.text;
			if (draft.activity === 'Streaming') params.url = draft.url;
		}
		busy = account.name;
		delete errors[account.name];
		try {
			await fleet.act(name, clear ? 'clear-status' : 'set-status', params);
			if (clear) drafts[account.name] = { presence: 'Online', activity: 'none', text: '', url: '' };
		} catch (failure) {
			errors[account.name] = failure instanceof Error ? failure.message : 'Failed.';
		} finally {
			busy = null;
		}
	}

	let allowed = $derived(fleet.may(name));
	const select = 'admin-field min-h-8 py-0 pr-7 pl-2 text-xs';
</script>

<Cards
	items={[
		{ value: accounts.length, label: 'accounts' },
		{ value: players.length, label: 'playing' },
		{ value: players.reduce((n, player) => n + player.listeners, 0), label: 'listeners' },
		{ value: s.api ?? '—', label: 'reading from' }
	]}
/>

<h3 class="eyebrow mb-2">Accounts</h3>
<div class="admin-scroll">
	{#if !accounts.length}
		<p class="admin-empty">No accounts connected.</p>
	{:else}
		<table class="admin-table">
			<thead><tr><th>Account</th><th>Role</th><th>Guilds</th><th>Playing in</th><th>Status</th></tr></thead>
			<tbody>
				{#each accounts as account (account.name)}
					<tr>
						<td class="wrap">{account.username}</td>
						<td>{account.master ? 'master' : 'secondary'}</td>
						<td class="mono">{account.guilds}</td>
						<td class="mono">{account.playing}</td>
						<td class="wrap">{statusText(account.status)}</td>
					</tr>
				{/each}
			</tbody>
		</table>
	{/if}
</div>

<h3 class="eyebrow mt-6 mb-2">Discord status</h3>
<div class="admin-scroll">
	{#each accounts as account (account.name)}
		{@const draft = drafts[account.name]}
		{#if draft}
		<div class="flex flex-wrap items-center gap-2 border-t border-haze px-3 py-2 first:border-t-0">
			<b class="min-w-28 text-[0.8125rem] font-semibold text-chalk">{account.name}</b>
			<select class={select} bind:value={draft.presence} aria-label="{account.name}: presence" disabled={!allowed}>
				{#each PRESENCES as presence (presence)}<option>{presence}</option>{/each}
			</select>
			<select class={select} bind:value={draft.activity} aria-label="{account.name}: activity" disabled={!allowed}>
				{#each ACTIVITIES as activity (activity)}<option>{activity}</option>{/each}
			</select>
			{#if draft.activity !== 'none'}
				<input
					class="admin-field min-h-8 max-w-80 min-w-32 flex-1 px-2 font-mono text-xs"
					placeholder="what it is doing"
					maxlength="128"
					aria-label="{account.name}: text"
					bind:value={draft.text}
					disabled={!allowed}
				/>
			{/if}
			{#if draft.activity === 'Streaming'}
				<input
					class="admin-field min-h-8 max-w-80 min-w-32 flex-1 px-2 font-mono text-xs"
					placeholder="https://twitch.tv/…"
					aria-label="{account.name}: stream URL"
					bind:value={draft.url}
					disabled={!allowed}
				/>
			{/if}
			<span>
				<button
					type="button"
					class="admin-button"
					disabled={!allowed || busy === account.name}
					title={allowed ? undefined : fleet.needs(name)}
					onclick={() => save(account, false)}>Set</button
				>
				{#if account.status}
					<button
						type="button"
						class="admin-button"
						disabled={!allowed || busy === account.name}
						title={allowed ? undefined : fleet.needs(name)}
						onclick={() => save(account, true)}>Clear</button
					>
				{/if}
			</span>
			{#if errors[account.name]}<span class="w-full text-xs text-ember" role="alert">{errors[account.name]}</span>{/if}
		</div>
		{/if}
	{:else}
		<p class="admin-empty">No accounts connected.</p>
	{/each}
</div>

<h3 class="eyebrow mt-6 mb-2">Players</h3>
<div class="admin-scroll">
	{#if !players.length}
		<p class="admin-empty">Nothing here right now.</p>
	{:else}
		<table class="admin-table">
			<thead>
				<tr><th>Account</th><th>Guild</th><th>Channel</th><th>Listeners</th><th>Playing</th><th>Queue</th><th>Position</th><th>Loop</th></tr>
			</thead>
			<tbody>
				{#each players as player, index (index)}
					<tr>
						<td class="wrap">{player.account}</td>
						<td class="wrap">{player.guild ?? '—'}</td>
						<td class="wrap">{player.channel ?? '—'}</td>
						<td class="mono">{player.listeners}</td>
						<td class="wrap">{player.track ? `${player.track}${player.requester ? ` (${player.requester})` : ''}` : '—'}</td>
						<td class="mono">{player.queueLength ? `${player.currentIndex + 1}/${player.queueLength}` : 'empty'}</td>
						<td class="mono">
							{player.paused ? `${player.elapsed} paused` : `${player.elapsed}${player.length ? ` / ${player.length}` : ''}`}
						</td>
						<td>{player.loop === 'None' ? '—' : player.loop}</td>
					</tr>
				{/each}
			</tbody>
		</table>
	{/if}
</div>

<h3 class="eyebrow mt-6 mb-2">Audit</h3>
<div class="admin-scroll">
	{#if !s.events?.length}
		<p class="admin-empty">Nothing here right now.</p>
	{:else}
		<table class="admin-table">
			<thead><tr><th>When</th><th>What</th><th>Account</th><th>Guild</th><th>Channel</th><th>Who</th><th>Detail</th></tr></thead>
			<tbody>
				{#each s.events.slice().reverse() as event, index (index)}
					<tr>
						<td>{ago(event.at)} ago</td>
						<td class="mono">{event.kind}</td>
						<td class="wrap">{event.account}</td>
						<td class="wrap">{event.guild ?? '—'}</td>
						<td class="wrap">{event.channel ?? '—'}</td>
						<td class="wrap">{event.user ?? '—'}</td>
						<td class="wrap">{event.detail}</td>
					</tr>
				{/each}
			</tbody>
		</table>
	{/if}
</div>
