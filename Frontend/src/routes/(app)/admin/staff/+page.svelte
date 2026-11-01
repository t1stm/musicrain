<script lang="ts">
	import { Icon, Key } from 'svelte-hero-icons';
	import { closeOnBack } from '$lib/backWatcher.svelte';
	import Skeleton from '$components/Skeleton.svelte';
	import { demote, grant, listStaff, type StaffList, type StaffMember } from '$requests/staff';
	import account from '$states/account.svelte';

	// Who can change what, as a grid: a row per staff account, a pad per permission. A lit pad is a
	// grant. Toggling marks the pad until Save sends the row; what Save changed ripples as it lands.

	let list = $state<StaffList | null>(null);
	let failure = $state('');

	/** Unsaved toggles, by username. A row with no entry shows what Dom last said. */
	let drafts = $state<Record<string, string[]>>({});
	/** Pads that just saved, by username, for the ripple. */
	let landed = $state<Record<string, string[]>>({});
	/** What the last change said, by username, under that row. */
	let notes = $state<Record<string, { text: string; failed: boolean }>>({});
	let saving = $state<string | null>(null);
	/** The account whose Remove is asking to be confirmed. */
	let confirming = $state<string | null>(null);
	let removed = $state('');

	let typed = $state('');
	let adding = $state(false);
	let added = $state<{ text: string; failed: boolean } | null>(null);

	closeOnBack(
		() => confirming !== null,
		() => (confirming = null)
	);

	const message = (error: unknown) =>
		error instanceof Error ? error.message : 'Could not reach the audio service.';

	async function load() {
		failure = '';
		try {
			list = await listStaff();
		} catch (error) {
			failure = message(error);
		}
	}

	$effect(() => {
		if (account.owner) load();
	});

	const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();
	const shown = (member: StaffMember) => drafts[member.username] ?? member.permissions;
	const pending = (member: StaffMember, name: string) =>
		shown(member).includes(name) !== member.permissions.includes(name);
	const dirty = (member: StaffMember) => list!.permissions.some(({ name }) => pending(member, name));

	function toggle(member: StaffMember, name: string) {
		const now = shown(member);
		drafts[member.username] = now.includes(name) ? now.filter((n) => n !== name) : [...now, name];
		delete notes[member.username];
	}

	function replace(saved: StaffMember) {
		list!.staff = list!.staff.map((member) => (same(member.username, saved.username) ? saved : member));
	}

	async function save(member: StaffMember) {
		saving = member.username;
		try {
			const saved = await grant(member.username, shown(member));
			landed[saved.username] = list!.permissions
				.map(({ name }) => name)
				.filter((name) => saved.permissions.includes(name) !== member.permissions.includes(name));
			delete drafts[member.username];
			replace(saved);
			notes[saved.username] = { text: 'Saved.', failed: false };
		} catch (error) {
			notes[member.username] = { text: message(error), failed: true };
		} finally {
			saving = null;
		}
	}

	async function remove(member: StaffMember) {
		confirming = null;
		try {
			await demote(member.username);
			list!.staff = list!.staff.filter((other) => other !== member);
			delete drafts[member.username];
			removed = `${member.username} is no longer on the staff.`;
		} catch (error) {
			notes[member.username] = { text: message(error), failed: true };
		}
	}

	async function promote(event: SubmitEvent) {
		event.preventDefault();
		const name = typed.trim();
		if (!name || !list) return;

		// a PUT to somebody already on the staff would wipe what they hold
		const already = list.staff.find((member) => same(member.username, name));
		if (already) {
			added = { text: `${already.username} is already on the staff.`, failed: true };
			return;
		}

		adding = true;
		try {
			const saved = await grant(name, []);
			list.staff = [...list.staff, saved];
			typed = '';
			added = {
				text: `${saved.username} is an admin now. Light the pads for what they may change, then save.`,
				failed: false
			};
		} catch (error) {
			added = { text: message(error), failed: true };
		} finally {
			adding = false;
		}
	}

	const small =
		'min-h-11 rounded-row px-3 text-sm font-semibold disabled:opacity-60 sm:min-h-9';
</script>

<svelte:head><title>Staff · Admin · musicrain</title></svelte:head>

<h1 class="font-display text-lg leading-tight font-light tracking-tight text-chalk sm:text-2xl">
	Staff
</h1>

{#if !account.owner}
	<p class="mt-2 max-w-lg text-sm text-fog">Only the owner manages staff.</p>
{:else}
	<p class="mt-2 max-w-lg text-sm text-fog">
		Admins can see everything in the panel. What they can change is what you light here.
	</p>

	{#if failure}
		<div class="mt-6 flex flex-wrap items-center gap-3">
			<p class="text-sm text-ember" role="alert">{failure}</p>
			<button
				type="button"
				class="{small} border border-haze text-chalk hover:bg-surface-200"
				onclick={load}>Try again</button
			>
		</div>
	{:else if !list}
		<div class="mt-7 flex max-w-3xl flex-col gap-3" aria-busy="true" aria-label="Loading">
			<Skeleton class="h-4 w-full max-w-md" />
			<Skeleton class="h-11 w-full" />
			<Skeleton class="h-11 w-full" />
		</div>
	{:else}
		{@const permissions = list.permissions}

		{#if list.keyWaiting}
			<p
				class="mt-6 flex max-w-xl gap-3 rounded-panel border border-haze bg-surface-100 p-3 text-[0.8125rem] leading-normal text-fog"
			>
				<Icon src={Key} micro class="mt-0.5 size-4 shrink-0 text-chalk" />
				<span>
					An unused administrator key is waiting. Whoever enters it joins the staff as an admin who can
					only look. To void it, delete <code class="font-mono text-chalk">/data/admin.key</code> on the server.
				</span>
			</p>
		{/if}

		<!-- the key to the grid: what each column unlocks -->
		<dl class="mt-7 grid max-w-3xl gap-x-8 gap-y-2.5 sm:grid-cols-2">
			{#each permissions as permission (permission.name)}
				<div class="flex gap-3">
					<dt class="w-18 shrink-0 pt-px font-mono text-xs text-chalk">{permission.name}</dt>
					<dd class="text-[0.8125rem] leading-snug text-fog">{permission.description}</dd>
				</div>
			{/each}
		</dl>

		<section class="mt-8 max-w-3xl" style="--n: {permissions.length}">
			<h2 class="eyebrow mb-2 flex items-center gap-3">
				Who can change what · {list.staff.length}
				<span class="h-px flex-1 bg-haze"></span>
			</h2>

			<!-- channel labels, read bottom to top like a mixing desk's strips -->
			<div class="strip pb-2" aria-hidden="true">
				<span class="spacer"></span>
				{#each permissions as permission (permission.name)}
					<span class="channel eyebrow">{permission.name}</span>
				{/each}
				<span class="spacer"></span>
			</div>

			<ul>
				{#each list.staff as member (member.username)}
					{@const fixed = member.role === 'owner'}
					<li class="strip border-t border-haze py-2.5">
						<div class="head">
							<div class="who min-w-0">
								<p class="truncate text-[0.9375rem] leading-snug text-chalk">
									{member.username}{#if same(member.username, account.username ?? '')}<span
											class="text-fog">&nbsp;· you</span
										>{/if}
								</p>
								<p class="text-[0.8125rem] leading-snug text-fog">
									{fixed
										? 'Owner, every permission'
										: member.permissions.length
											? 'Admin'
											: 'Admin, can only look'}
								</p>
							</div>
							<div class="acts flex shrink-0 gap-1.5">
								{#if !fixed}
									{#if dirty(member)}
										<button
											type="button"
											disabled={saving !== null}
											class="{small} bg-primary-600 text-white hover:bg-primary-0"
											onclick={() => save(member)}
										>
											{saving === member.username ? 'Saving…' : 'Save'}
										</button>
									{/if}
									<button
										type="button"
										class="{small} font-normal text-fog hover:bg-surface-200 hover:text-chalk"
										onclick={() => (confirming = member.username)}
									>
										Remove
									</button>
								{/if}
							</div>
						</div>

						{#each permissions as permission (permission.name)}
							{@const on = shown(member).includes(permission.name)}
							{@const rippling = landed[member.username]?.includes(permission.name)}
							{#if fixed}
								<span class="pad" data-on aria-hidden="true"></span>
							{:else}
								<label
									class="pad {rippling ? 'animate-ripple motion-reduce:animate-none' : ''}"
									data-on={on || undefined}
									data-pending={pending(member, permission.name) || undefined}
									title={permission.description}
									onanimationend={() =>
										(landed[member.username] = landed[member.username].filter(
											(name) => name !== permission.name
										))}
								>
									<input
										type="checkbox"
										class="sr-only"
										checked={on}
										disabled={saving === member.username}
										aria-label="{member.username}: {permission.name}"
										onchange={() => toggle(member, permission.name)}
									/>
								</label>
							{/if}
						{/each}

						{#if confirming === member.username}
							<div class="row-wide pt-3">
								<p class="text-[0.9375rem] text-chalk">Remove {member.username} from the staff?</p>
								<p class="mt-1 mb-3 text-[0.8125rem] leading-normal text-fog">
									They lose the panel and every permission. Their account stays as it is.
								</p>
								<div class="flex gap-2">
									<button
										type="button"
										class="min-h-11 rounded-row border border-ember px-4 text-sm font-semibold text-ember hover:bg-surface-200"
										onclick={() => remove(member)}
									>
										Remove from staff
									</button>
									<button
										type="button"
										class="min-h-11 rounded-row border border-haze px-4 text-sm font-semibold text-chalk hover:bg-surface-200"
										onclick={() => (confirming = null)}
									>
										Keep
									</button>
								</div>
							</div>
						{/if}

						{#if notes[member.username]}
							<p
								class="row-wide pt-2 text-[0.8125rem] {notes[member.username].failed
									? 'text-ember'
									: 'text-fog'}"
								aria-live="polite"
							>
								{notes[member.username].text}
							</p>
						{/if}
					</li>
				{/each}
			</ul>

			{#if removed}
				<p class="mt-3 text-sm text-fog" aria-live="polite">{removed}</p>
			{/if}
		</section>

		<section class="mt-9 max-w-md">
			<h2 class="eyebrow flex items-center gap-3">
				Make an admin
				<span class="h-px flex-1 bg-haze"></span>
			</h2>
			<form class="mt-3 flex flex-col gap-1.5" onsubmit={promote}>
				<label for="new-admin" class="text-[0.8125rem] text-fog">Their username</label>
				<div class="flex gap-2">
					<input
						id="new-admin"
						type="text"
						bind:value={typed}
						maxlength="32"
						autocomplete="off"
						autocapitalize="none"
						spellcheck="false"
						class="min-h-11 w-full min-w-0 rounded-row border border-haze bg-dark-0 text-sm text-chalk ring-primary-0 focus:border-primary-0 focus-visible:ring-2 sm:min-h-9"
					/>
					<button
						type="submit"
						disabled={!typed.trim() || adding}
						class="min-h-11 shrink-0 rounded-row border border-haze px-4 text-sm font-semibold text-chalk hover:bg-surface-200 disabled:opacity-60 sm:min-h-9"
					>
						Make admin
					</button>
				</div>
				<p class="text-[0.8125rem] text-fog">They start able to look and change nothing.</p>
				{#if added}
					<p class="text-sm {added.failed ? 'text-ember' : 'text-primary-500'}" aria-live="polite">
						{added.text}
					</p>
				{/if}
			</form>
		</section>
	{/if}
{/if}

<style>
	/* One grid for the labels and every row, so the pads line up in columns. On a phone the
	   name and its buttons take the first line and the pads share the width under it; wider,
	   everything sits on one line with the pads at a fixed size. */
	.strip {
		--pad: 2.75rem;
		display: grid;
		grid-template-columns: repeat(var(--n), minmax(0, 1fr));
		column-gap: 0.375rem;
		row-gap: 0.5rem;
		align-items: center;
	}

	.head {
		grid-column: 1 / -1;
		display: flex;
		align-items: center;
		gap: 0.75rem;
	}

	.who {
		flex: 1;
	}

	.spacer {
		display: none;
	}

	.row-wide {
		grid-column: 1 / -1;
	}

	.channel {
		justify-self: center;
		writing-mode: vertical-rl;
		rotate: 180deg;
		white-space: nowrap;
	}

	@media (min-width: 40rem) {
		.strip {
			grid-template-columns: minmax(0, 1fr) repeat(var(--n), var(--pad)) 11rem;
			column-gap: 0.5rem;
			row-gap: 0;
		}

		/* name, pads, buttons: the head's two halves become cells of their own */
		.head {
			display: contents;
		}

		.who {
			grid-column: 1;
			grid-row: 1;
		}

		.acts {
			grid-column: -2 / -1;
			grid-row: 1;
			justify-self: end;
		}

		.spacer {
			display: block;
		}
	}

	.pad {
		position: relative;
		height: var(--pad);
		border: 1px solid var(--color-haze);
		border-radius: var(--radius-art);
		background: var(--color-dark-0);
		cursor: pointer;
		transition:
			background-color 120ms ease,
			border-color 120ms ease,
			box-shadow 160ms ease;
	}

	label.pad:hover {
		border-color: var(--color-primary-0);
	}

	.pad[data-on] {
		border-color: var(--color-primary-0);
		background: var(--color-primary-600);
		box-shadow:
			inset 0 1px 0 color-mix(in srgb, var(--color-primary-400) 45%, transparent),
			0 0 18px -6px var(--color-primary-0);
	}

	/* the owner's row: lit and fixed */
	span.pad {
		cursor: default;
		opacity: 0.45;
	}

	.pad:has(input:focus-visible) {
		outline: 2px solid var(--color-primary-500);
		outline-offset: 2px;
	}

	.pad:has(input:disabled) {
		cursor: progress;
	}

	/* changed and not saved yet: what Save will send */
	.pad[data-pending]::after {
		content: '';
		position: absolute;
		top: 4px;
		right: 4px;
		width: 6px;
		height: 6px;
		border-radius: 50%;
		background: var(--color-primary-200);
	}

	.pad[data-on][data-pending]::after {
		background: var(--color-chalk);
	}

	@media (prefers-reduced-motion: reduce) {
		.pad {
			transition: none;
		}
	}
</style>
