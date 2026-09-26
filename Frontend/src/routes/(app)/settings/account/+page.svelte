<script lang="ts">
	import { resolve } from '$app/paths';
	import account from '$states/account.svelte';

	let accountName = $state('');
	let accountPassword = $state('');

	/** What the last change said, under the form it came from. */
	let notes = $state<Record<string, { text: string; failed: boolean }>>({});

	let newName = $state('');
	let renamePassword = $state('');
	let currentPassword = $state('');
	let nextPassword = $state('');
	let deletePassword = $state('');
	let deleted = $state(false);

	async function signIn(create: boolean) {
		deleted = false;
		if (create) await account.signUp(accountName, accountPassword);
		else await account.signIn(accountName, accountPassword);
		accountPassword = '';
	}

	/** Runs one change and files what it said under `form`. */
	async function run<T>(
		form: string,
		change: Promise<{ value?: T; error?: string }>,
		success: (value: T) => string
	) {
		const { value, error } = await change;
		notes[form] = error ? { text: error, failed: true } : { text: success(value as T), failed: false };
		return !error;
	}

	async function submitRename(event: SubmitEvent) {
		event.preventDefault();
		if (await run('rename', account.rename(newName, renamePassword), () => 'Username changed.'))
			newName = '';
		renamePassword = '';
	}

	async function submitPassword(event: SubmitEvent) {
		event.preventDefault();
		await run(
			'password',
			account.changePassword(currentPassword, nextPassword),
			() => 'Password changed. Your other devices are signed out.'
		);
		currentPassword = nextPassword = '';
	}

	async function signOutEverywhere() {
		await run('everywhere', account.signOutEverywhere(), (revoked) =>
			revoked === 0
				? 'No other devices were signed in.'
				: `Signed out ${revoked} other ${revoked === 1 ? 'device' : 'devices'}.`
		);
	}

	async function submitDelete(event: SubmitEvent) {
		event.preventDefault();
		const { error } = await account.deleteAccount(deletePassword);
		deletePassword = '';
		if (error) notes.delete = { text: error, failed: true };
		else deleted = true;
	}

	const field =
		'min-h-11 w-full rounded-row border border-haze bg-dark-0 text-sm text-chalk placeholder:text-fog ring-primary-0 focus:border-primary-0 focus-visible:ring-2 sm:min-h-0';
	const primary =
		'min-h-11 rounded-row bg-primary-600 px-4 text-sm font-semibold text-white hover:bg-primary-0 disabled:opacity-60 sm:min-h-9';
	const quiet =
		'min-h-11 rounded-row border border-haze px-4 text-sm font-semibold text-fog hover:bg-surface-200 hover:text-chalk disabled:opacity-60 sm:min-h-9';
	const row =
		'flex min-h-12 cursor-pointer list-none items-center justify-between gap-3 text-sm text-chalk marker:hidden [&::-webkit-details-marker]:hidden';
</script>

<svelte:head><title>Account · Settings · musicrain</title></svelte:head>

<h1 class="font-display text-lg leading-tight font-light tracking-tight text-chalk sm:text-2xl">
	Account
</h1>

{#snippet note(form: string)}
	{#if notes[form]}
		<p class="text-sm {notes[form].failed ? 'text-ember' : 'text-fog'}" aria-live="polite">
			{notes[form].text}
		</p>
	{/if}
{/snippet}

{#if !account.signedIn}
	<section class="mt-6 flex max-w-sm flex-col gap-3">
		{#if deleted}
			<p class="text-sm text-fog" aria-live="polite">Your account and its playlists are deleted.</p>
		{/if}
		<p class="text-sm text-fog">
			Signed out. Your settings are saved on this device. Sign in to keep playlists and use your
			settings on your other devices.
		</p>
		<form
			class="flex flex-col gap-2"
			onsubmit={(event) => {
				event.preventDefault();
				signIn(false);
			}}
		>
			<input
				type="text"
				id="account-username"
				name="username"
				bind:value={accountName}
				maxlength="32"
				autocomplete="username"
				autocapitalize="none"
				spellcheck="false"
				placeholder="Username"
				aria-label="Username"
				class={field}
			/>
			<input
				type="password"
				id="account-password"
				name="password"
				bind:value={accountPassword}
				maxlength="256"
				autocomplete="current-password"
				placeholder="Password"
				aria-label="Password"
				class={field}
			/>
			{#if account.error}<p class="text-sm text-ember">{account.error}</p>{/if}
			<div class="mt-1 flex gap-2">
				<button type="submit" disabled={account.busy} class="flex-1 {primary}">
					{account.busy ? 'Working…' : 'Sign in'}
				</button>
				<button
					type="button"
					disabled={account.busy}
					class="flex-1 {quiet} text-chalk"
					onclick={() => signIn(true)}
				>
					Create an account
				</button>
			</div>
		</form>
	</section>
{:else}
	<section class="mt-6 flex flex-col gap-4">
		<p class="text-sm text-chalk">
			Signed in as <b class="font-semibold">{account.username}</b>
		</p>
		<a
			href={resolve('/playlists')}
			class="w-fit text-sm text-primary-500 underline-offset-4 hover:underline">Your playlists</a
		>
		<div class="flex flex-wrap gap-2">
			<button type="button" class={quiet} onclick={() => account.signOut()}>Sign out</button>
			<button type="button" disabled={account.busy} class={quiet} onclick={signOutEverywhere}>
				Sign out everywhere else
			</button>
		</div>
		{@render note('everywhere')}
	</section>

	<!-- One disclosure per change: each form stays on the page without the others crowding
	     it, and a native `details` needs no script to open. -->
	<section class="mt-9 flex flex-col">
		<h2 class="eyebrow flex items-center gap-3">
			Change your account
			<span class="h-px flex-1 bg-haze"></span>
		</h2>

		<details class="group border-b border-haze">
			<summary class={row}>
				Username
				<span class="font-mono text-xs text-fog group-open:hidden">{account.username}</span>
			</summary>
			<form class="flex max-w-sm flex-col gap-2 pb-4" onsubmit={submitRename}>
				<input
					type="text"
					name="new-username"
					bind:value={newName}
					maxlength="32"
					autocomplete="username"
					autocapitalize="none"
					spellcheck="false"
					placeholder="New username"
					aria-label="New username"
					class={field}
				/>
				<input
					type="password"
					name="password"
					bind:value={renamePassword}
					maxlength="256"
					autocomplete="current-password"
					placeholder="Your password"
					aria-label="Your password"
					class={field}
				/>
				{@render note('rename')}
				<button type="submit" disabled={account.busy} class="w-fit {primary}">Change username</button>
			</form>
		</details>

		<details class="border-b border-haze">
			<summary class={row}>Password</summary>
			<form class="flex max-w-sm flex-col gap-2 pb-4" onsubmit={submitPassword}>
				<!-- a hidden username, so a password manager knows which account this new password is for -->
				<input type="text" autocomplete="username" value={account.username} hidden readonly />
				<input
					type="password"
					name="current-password"
					bind:value={currentPassword}
					maxlength="256"
					autocomplete="current-password"
					placeholder="Current password"
					aria-label="Current password"
					class={field}
				/>
				<input
					type="password"
					name="new-password"
					bind:value={nextPassword}
					minlength="8"
					maxlength="256"
					autocomplete="new-password"
					placeholder="New password, at least 8 characters"
					aria-label="New password"
					class={field}
				/>
				<p class="text-sm text-fog">Changing it signs out your other devices.</p>
				{@render note('password')}
				<button type="submit" disabled={account.busy} class="w-fit {primary}">Change password</button>
			</form>
		</details>

		<details class="border-b border-haze">
			<summary class="{row} text-ember">Delete account</summary>
			<form class="flex max-w-sm flex-col gap-2 pb-4" onsubmit={submitDelete}>
				<p class="text-sm text-fog">
					Deletes your account and every playlist you made. This can't be undone.
				</p>
				<input
					type="password"
					name="password"
					bind:value={deletePassword}
					maxlength="256"
					autocomplete="current-password"
					placeholder="Your password"
					aria-label="Your password"
					class={field}
				/>
				{@render note('delete')}
				<button
					type="submit"
					disabled={account.busy || !deletePassword}
					class="min-h-11 w-fit rounded-row bg-ember px-4 text-sm font-semibold text-white hover:brightness-110 disabled:opacity-60 sm:min-h-9"
				>
					Delete account
				</button>
			</form>
		</details>
	</section>
{/if}
