<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { resolve } from '$app/paths';
	import AcceptGate from '$components/friends/AcceptGate.svelte';
	import InviteGate from '$components/friends/InviteGate.svelte';
	import { closeOnBack } from '$lib/backWatcher.svelte';
	import account from '$states/account.svelte';
	import friends, { minutesAndSeconds, secondsLeft } from '$states/friends.svelte';
	import playlists from '$states/playlists.svelte';

	// Read from the URL every time it changes, not once: a second link can arrive while this
	// page is already open (the installed app routes it here in place), and only the query moves.
	let code = $derived(page.url.searchParams.get('code'));
	/**
	 * The code being asked about. It comes off the URL as soon as it arrives, so a refresh
	 * or a back press never asks twice, and the prompt opens only after that: its own back
	 * entry has to sit on the clean URL, not be replaced by it.
	 */
	let asking = $state<string | null>(null);

	$effect(() => {
		const arrived = code;
		if (!arrived) return;
		goto(resolve('/settings/friends'), { replaceState: true, noScroll: true, keepFocus: true }).then(
			() => (asking = arrived)
		);
	});

	let inviting = $state(false);
	let typed = $state('');
	/** The friend whose Remove is asking to be confirmed. */
	let confirming = $state<string | null>(null);
	/** Names that arrived while this page was open; their rows land with the ripple. */
	let landed = $state<string[]>([]);
	let note = $state('');
	let now = $state(Date.now());

	closeOnBack(
		() => confirming !== null,
		() => (confirming = null)
	);

	$effect(() => {
		if (!account.token) return;
		friends.load();
		// a code still live from earlier — on this device or another — shows on the button
		friends.pollInvite();
		// what each friend edits, for the line under their name
		playlists.loadMine();
	});

	// Everybody on the first read is simply there; anybody after it just arrived.
	let seen: Set<string> | null = null;
	$effect(() => {
		const list = friends.list;
		if (!friends.loaded) return;
		if (seen) {
			const fresh = list.filter((name) => !seen!.has(name));
			if (fresh.length) landed = [...landed, ...fresh];
		}
		seen = new Set(list);
	});

	let live = $derived(!!friends.invite && !friends.ended);
	let left = $derived(friends.invite ? secondsLeft(friends.invite.expiresUtc, now) : 0);

	// the button's countdown, while a code is live and the gate is closed
	$effect(() => {
		if (!live || inviting) return;
		const clock = setInterval(() => (now = Date.now()), 1000);
		return () => clearInterval(clock);
	});

	const same = (a: string, b: string | null) => !!b && a.toLowerCase() === b.toLowerCase();

	/** What ties you to a friend, from the playlists you can edit: who edits whose. */
	function ties(name: string) {
		const theirs = playlists.mine.filter((p) => same(p.owner, name)).map((p) => p.name);
		const yours = playlists.mine
			.filter((p) => p.owner === account.username && p.collaborators.some((c) => same(c, name)))
			.map((p) => p.name);
		const named = (list: string[]) => (list.length === 1 ? list[0] : `${list.length} playlists`);

		return [
			yours.length ? `Edits ${named(yours)}` : '',
			theirs.length ? `${yours.length ? 'you' : 'You'} edit ${named(theirs)}` : ''
		]
			.filter(Boolean)
			.join(' · ');
	}

	async function showCode() {
		now = Date.now();
		if (await friends.openInvite()) inviting = true;
	}

	function accepted({ username, alreadyFriends }: { username: string; alreadyFriends: boolean }) {
		note = alreadyFriends ? `You and ${username} are already friends.` : `You and ${username} are friends now.`;
		asking = null;
	}

	async function addTyped(event: SubmitEvent) {
		event.preventDefault();
		if (!typed.trim()) return;

		const answer = await friends.accept(typed);
		if (!answer) return;
		typed = '';
		accepted(answer);
	}

	async function remove(name: string) {
		confirming = null;
		await friends.remove(name);
		// their playlists and yours lose each other's names in the same stroke
		playlists.loadMine();
	}

	const monogram =
		'grid size-9 shrink-0 place-items-center rounded-full bg-surface-200 text-sm font-semibold text-primary-500';
</script>

<svelte:head><title>Friends · Settings · musicrain</title></svelte:head>

<h1 class="font-display text-lg leading-tight font-light tracking-tight text-chalk sm:text-2xl">
	Friends
</h1>
<p class="mt-2 max-w-lg text-sm text-fog">
	Friends can open playlists you share with friends, and you can let them edit yours.
</p>

{#if !account.signedIn}
	<p class="mt-6 max-w-lg text-sm text-fog">
		<a
			href={`${resolve('/settings/account')}?next=${encodeURIComponent(resolve('/settings/friends'))}`}
			class="text-primary-500 underline-offset-4 hover:underline">Sign in</a
		> to add friends.
	</p>
{:else}
	<section class="mt-6 flex max-w-md flex-col gap-3">
		<h2 class="eyebrow flex items-center gap-3">
			Add friends
			<span class="h-px flex-1 bg-haze"></span>
		</h2>
		<button
			type="button"
			class="min-h-12 rounded-row bg-primary-600 px-4 text-sm font-semibold text-white hover:bg-primary-0 disabled:opacity-60"
			onclick={showCode}
		>
			Show my code{#if live && left > 0}<span class="font-mono font-normal opacity-80"
					>&nbsp;· {minutesAndSeconds(left)} left</span
				>{/if}
		</button>

		<form class="flex flex-col gap-1.5" onsubmit={addTyped}>
			<label for="friend-code" class="text-[0.8125rem] text-fog">Have a code?</label>
			<div class="flex gap-2">
				<input
					id="friend-code"
					type="text"
					bind:value={typed}
					placeholder="K7QM-3XRD"
					autocapitalize="characters"
					autocomplete="off"
					spellcheck="false"
					enterkeyhint="go"
					maxlength="9"
					class="min-h-11 w-full min-w-0 rounded-row border border-haze bg-dark-0 font-mono tracking-[0.12em] text-chalk uppercase placeholder:text-surface-400 ring-primary-0 focus:border-primary-0 focus-visible:ring-2"
				/>
				<button
					type="submit"
					disabled={!typed.trim()}
					class="min-h-11 shrink-0 rounded-row border border-haze px-4 text-sm font-semibold text-chalk hover:bg-surface-200 disabled:opacity-60"
				>
					Add friend
				</button>
			</div>
		</form>

		{#if note}
			<p class="text-sm text-primary-500" aria-live="polite">{note}</p>
		{/if}
		{#if friends.error && !inviting && !asking}
			<p class="text-sm text-ember" role="alert">{friends.error}</p>
		{/if}
	</section>

	<section class="mt-7 flex max-w-md flex-col">
		<h2 class="eyebrow mb-1 flex items-center gap-3">
			Your friends{friends.list.length ? ` · ${friends.list.length}` : ''}
			<span class="h-px flex-1 bg-haze"></span>
		</h2>

		{#if friends.loaded && friends.list.length === 0}
			<p class="mt-1 text-sm text-fog">
				No friends yet. Show your code to the people you’re with, or send the link to a group chat.
			</p>
		{:else}
			<ul>
				{#each friends.list as name (name)}
					{@const fresh = landed.includes(name)}
					<li class="border-b border-haze">
						{#if confirming === name}
							<div class="py-3">
								<p class="text-[0.9375rem] text-chalk">Remove {name}?</p>
								<p class="mt-1 mb-3 text-[0.8125rem] leading-normal text-fog">
									You stop editing each other’s playlists. Tracks either of you added stay where they
									are.
								</p>
								<div class="flex gap-2">
									<button
										type="button"
										class="min-h-11 rounded-row border border-ember px-4 text-sm font-semibold text-ember hover:bg-surface-200"
										onclick={() => remove(name)}
									>
										Remove
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
						{:else}
							<div class="flex min-h-15 items-center gap-3">
								<span
									aria-hidden="true"
									class="{monogram} {fresh ? 'animate-ripple motion-reduce:animate-none' : ''}"
									>{name[0]}</span
								>
								<div class="min-w-0 flex-1">
									<p class="truncate text-[0.9375rem] leading-snug text-chalk">{name}</p>
									{#if fresh || ties(name)}
										<p class="truncate text-[0.8125rem] leading-snug text-fog">
											{fresh ? 'Added just now' : ties(name)}
										</p>
									{/if}
								</div>
								<button
									type="button"
									class="min-h-11 shrink-0 rounded-row px-2.5 text-sm text-fog hover:bg-surface-200 hover:text-chalk"
									onclick={() => (confirming = name)}
								>
									Remove
								</button>
							</div>
						{/if}
					</li>
				{/each}
			</ul>
		{/if}
	</section>

	<InviteGate bind:open={inviting} />
{/if}

{#if asking}
	{#key asking}
		<AcceptGate
			code={asking}
			onclose={() => (asking = null)}
			onaccepted={accepted}
			onownCode={() => {
				asking = null;
				showCode();
			}}
		/>
	{/key}
{/if}
