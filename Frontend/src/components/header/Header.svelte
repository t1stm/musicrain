<script lang="ts">
	import { flushSync } from 'svelte';
	import { Beaker, Cloud, FolderOpen, Icon, MagnifyingGlass, RectangleStack, User } from 'svelte-hero-icons';
	import { page } from '$app/state';
	import { resolve } from '$app/paths';
	import account from '$states/account.svelte';
	import session from '$states/session.svelte';
	import user from '$states/user.svelte';
	import { closeOnBack } from '$lib/backWatcher.svelte';
	import { dismiss } from '$lib/dismiss';

	// The account panel drops from the header over the page, and the queue/chat sheet sits
	// over the page too — the panel would open underneath it. Opening one closes the other.
	let { onaccount }: { onaccount?: () => void } = $props();

	const isAlpha = true;
	let searchTerm = $derived(page.url.searchParams.get('term') ?? '');
	let editingName = $state(false);
	let draftName = $state('');
	let accountName = $state('');
	let accountPassword = $state('');

	closeOnBack(
		() => editingName,
		() => (editingName = false)
	);

	function openName() {
		draftName = user.username ?? '';
		if (editingName) return (editingName = false);
		// Two flushes, the sheet's close first, so the panel takes over the sheet's history
		// entry (see `closeOnBack`) instead of stacking a second one on it: in a single flush
		// this component's effects run before the layout's, and the panel would open first.
		flushSync(() => onaccount?.());
		flushSync(() => (editingName = true));
	}

	function saveName(event: SubmitEvent) {
		event.preventDefault();
		user.choose(draftName);
		editingName = false;
	}

	/** Signing in fills the name field too, so the panel does not contradict itself. */
	async function signIn(create: boolean) {
		const done = create
			? await account.signUp(accountName, accountPassword)
			: await account.signIn(accountName, accountPassword);
		accountPassword = '';
		if (done) draftName = user.username ?? draftName;
	}
</script>

<header
	class="box-border flex h-12 w-full shrink-0 justify-center bg-surface-0 px-3 py-1.5 micro:hidden sm:h-14 sm:px-4 sm:py-2"
>
	<div class="flex h-full w-full justify-between gap-2">
		<!-- The mark and the way into the library travel together: one flex child, so
		     justify-between distributes its space around the pair instead of pushing the
		     folder off towards the search field. -->
		<div class="flex shrink-0 items-center gap-2 sm:mr-2">
			<a
				href={resolve('/')}
				class="flex items-center gap-1.5 rounded-row outline-none focus-visible:ring-2 focus-visible:ring-primary-500"
			>
				<span class="hidden font-display text-lg font-extralight tracking-tight text-chalk sm:inline"
					>music<b class="font-medium text-primary-500">rain</b></span
				>
				<Icon src={Cloud} solid class="size-6 shrink-0 text-primary-500" />
				{#if isAlpha}
					<Icon src={Beaker} micro class="mt-auto mb-1 hidden size-3.5 text-fog sm:block" />
				{/if}
			</a>

			<!-- Gold is the library everywhere in this app, so the way into it is gold on hover.
			     On a phone too: without them the playlists had no way in short of the account
			     panel. The search field gives up the width, and still holds its placeholder
			     at 320px. -->
			<a
				href={resolve('/browse')}
				aria-label="Browse the library by folder"
				class="flex size-9 shrink-0 items-center justify-center rounded-row border border-haze text-fog outline-none hover:border-gold hover:text-gold focus-visible:ring-2 focus-visible:ring-primary-500 sm:size-10"
				class:border-gold={page.url.pathname.startsWith('/browse')}
				class:text-gold={page.url.pathname.startsWith('/browse')}
			>
				<Icon src={FolderOpen} micro size="20" />
			</a>

			<!-- Playlists are not the library — they are what people cut out of it — so this
			     door is the app's own violet, not gold. -->
			<a
				href={resolve('/playlists')}
				aria-label="Playlists"
				class="flex size-9 shrink-0 items-center justify-center rounded-row border border-haze text-fog outline-none hover:border-primary-0 hover:text-primary-500 focus-visible:ring-2 focus-visible:ring-primary-500 sm:size-10"
				class:border-primary-0={page.url.pathname.startsWith('/playlist')}
				class:text-primary-500={page.url.pathname.startsWith('/playlist')}
			>
				<Icon src={RectangleStack} micro size="20" />
			</a>
		</div>

		<form class="flex min-w-0 gap-2 w-full max-w-lg" action="/search">
			<!-- `search` gives a phone's keyboard its Search key and the field its clear button -->
			<input
				type="search"
				enterkeyhint="search"
				name="term"
				bind:value={searchTerm}
				placeholder="Search"
				class="rounded-row border border-haze bg-dark-0 w-full text-chalk placeholder:text-fog ring-primary-0 focus:border-primary-0 focus-visible:ring-2"
			/>
			<!-- the form already submits on Enter; the button is a desktop affordance -->
			<button
				class="hidden size-10 min-w-10 cursor-pointer items-center justify-center rounded-row bg-primary-600 sm:flex"
				type="submit"
			>
				<Icon src={MagnifyingGlass} micro color="white" size="24" />
			</button>
		</form>

		<div class="relative shrink-0" {@attach editingName && dismiss(() => (editingName = false))}>
			<button
				type="button"
				aria-label={user.username ? `You are ${user.username}` : 'Set your name'}
				aria-expanded={editingName}
				class="flex size-9 shrink-0 cursor-pointer items-center justify-center overflow-hidden rounded-full bg-primary-600 outline-none focus-visible:ring-2 focus-visible:ring-primary-200 sm:size-10"
				onclick={openName}
			>
				{#if user.avatarUrl}
					<!-- a blocked or dead CDN URL drops back to the icon -->
					<img
						src={user.avatarUrl}
						alt=""
						class="size-full object-cover"
						onerror={() => (user.avatarUrl = null)}
					/>
				{:else}
					<Icon src={User} micro size="24" color="white" />
				{/if}
			</button>

			{#if editingName}
				<div
					class="absolute right-0 z-30 mt-2 w-[min(18rem,calc(100vw-1.5rem))] rounded-panel border border-haze bg-surface-100 p-3 text-left"
				>
					<form onsubmit={saveName}>
						<h2 class="eyebrow mb-2">Your name</h2>
						<input
							type="text"
							bind:value={draftName}
							maxlength="60"
							placeholder="Anonymous"
							aria-label="Your name"
							class="rounded-row border border-haze bg-dark-0 w-full text-sm text-chalk placeholder:text-fog ring-primary-0 focus:border-primary-0 focus-visible:ring-2"
						/>
						{#if session.inRoom}
							<!-- the server applies a name only when a connection registers -->
							<p class="mt-2 text-xs text-fog">
								Renaming reconnects you. The room sees you leave and come back.
							</p>
						{/if}
						<button
							type="submit"
							class="mt-3 w-full rounded-row bg-primary-600 px-3 py-1.5 text-sm font-semibold text-white hover:bg-primary-0"
						>
							Save name
						</button>
					</form>

					<!-- An account is a different thing from the name above: it is what playlists
					     belong to. No /login route — the popover is already the place a face opens. -->
					<section class="mt-4 border-t border-haze pt-3">
						{#if account.signedIn}
							<h2 class="eyebrow mb-2">Account</h2>
							<p class="truncate text-sm">
								Signed in as <b class="font-semibold">{account.username}</b>
							</p>
							<a
								href={resolve('/playlists')}
								class="mt-2 block text-sm text-primary-500 underline-offset-4 hover:underline"
								onclick={() => (editingName = false)}
							>
								Your playlists
							</a>
							<button
								type="button"
								class="mt-3 w-full rounded-row border border-haze px-3 py-1.5 text-sm font-semibold text-fog hover:bg-surface-200 hover:text-chalk"
								onclick={() => account.signOut()}
							>
								Sign out
							</button>
						{:else}
							<form
								onsubmit={(event) => {
									event.preventDefault();
									signIn(false);
								}}
							>
								<h2 class="eyebrow mb-2">Account</h2>
								<p class="mb-2 text-xs text-fog">Sign in to keep playlists.</p>
								<input
									type="text"
									bind:value={accountName}
									maxlength="32"
									autocomplete="username"
									placeholder="Username"
									aria-label="Username"
									class="rounded-row border border-haze bg-dark-0 w-full text-sm text-chalk placeholder:text-fog ring-primary-0 focus:border-primary-0 focus-visible:ring-2"
								/>
								<input
									type="password"
									bind:value={accountPassword}
									maxlength="256"
									autocomplete="current-password"
									placeholder="Password"
									aria-label="Password"
									class="rounded-row border border-haze bg-dark-0 mt-2 w-full text-sm text-chalk placeholder:text-fog ring-primary-0 focus:border-primary-0 focus-visible:ring-2"
								/>
								{#if account.error}<p class="mt-2 text-xs text-ember">{account.error}</p>{/if}
								<div class="mt-3 flex gap-2">
									<button
										type="submit"
										disabled={account.busy}
										class="flex-1 rounded-row bg-primary-600 px-3 py-1.5 text-sm font-semibold text-white hover:bg-primary-0 disabled:opacity-60"
									>
										{account.busy ? 'Working…' : 'Sign in'}
									</button>
									<button
										type="button"
										disabled={account.busy}
										class="flex-1 rounded-row border border-haze px-3 py-1.5 text-sm font-semibold hover:bg-surface-200 disabled:opacity-60"
										onclick={() => signIn(true)}
									>
										Create an account
									</button>
								</div>
							</form>
						{/if}
					</section>
				</div>
			{/if}
		</div>
	</div>
</header>
