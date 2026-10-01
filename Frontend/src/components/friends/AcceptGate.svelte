<script lang="ts">
	import { resolve } from '$app/paths';
	import WaterClock from './WaterClock.svelte';
	import { closeOnBack } from '$lib/backWatcher.svelte';
	import { peekInvite } from '$requests/accounts';
	import account from '$states/account.svelte';
	import friends, { secondsLeft, spaced } from '$states/friends.svelte';

	/**
	 * What a friend link opens to: whose code it is, and "add them?". Signed out — often
	 * somebody who has no account yet, standing next to the inviter — it says whose code
	 * it is and sends them to sign in or make an account, and back here after.
	 */
	let {
		code,
		onclose,
		onaccepted,
		onownCode
	}: {
		code: string;
		/** However it ends: accepted, declined, or nothing to accept. */
		onclose: () => void;
		onaccepted: (answer: { username: string; alreadyFriends: boolean }) => void;
		/** The visitor would rather show their own code. */
		onownCode: () => void;
	} = $props();

	let dialog = $state<HTMLDialogElement>();
	let peek = $state<{ username: string; expiresUtc: string } | 'invalid' | null>(null);
	let now = $state(Date.now());
	let busy = $state(false);

	$effect(() => {
		let live = true;
		peek = null;
		peekInvite(code)
			.then((found) => live && (peek = found))
			.catch(() => live && (peek = 'invalid'));

		return () => {
			live = false;
		};
	});

	// opens once there is something to say, so it never flashes empty
	$effect(() => {
		if (peek && dialog && !dialog.open) dialog.showModal();
	});

	// back, the back gesture and Escape say "Not now", as in every other gate
	closeOnBack(
		() => !!peek,
		() => dialog?.close()
	);

	$effect(() => {
		const clock = setInterval(() => (now = Date.now()), 1000);
		return () => clearInterval(clock);
	});

	let found = $derived(peek && peek !== 'invalid' ? peek : null);
	let left = $derived(found ? secondsLeft(found.expiresUtc, now) : 0);
	let invalid = $derived(peek === 'invalid' || (!!found && left <= 0));
	let own = $derived(!!found && found.username === account.username);
	let minutes = $derived(Math.max(1, Math.ceil(left / 60)));
	/** Back here once signed in — the account page follows `next`. The page has already taken the code off its URL. */
	let back = $derived(encodeURIComponent(`${resolve('/settings/friends')}?code=${code}`));

	async function accept() {
		busy = true;
		const answer = await friends.accept(code);
		busy = false;
		if (answer) onaccepted(answer);
	}

	const primary =
		'grid min-h-11 place-items-center rounded-row bg-primary-600 px-4 text-sm font-semibold text-white hover:bg-primary-0 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-200 disabled:opacity-60';
	const outline =
		'min-h-11 rounded-row border border-haze px-4 text-sm font-semibold text-chalk hover:bg-surface-200';
</script>

<dialog
	bind:this={dialog}
	class="gate"
	closedby="any"
	aria-labelledby="accept-title"
	aria-describedby="accept-body"
	onclose={onclose}
>
	<!-- the same clock the inviter's phone is showing -->
	<WaterClock {left} spent={invalid} />

	{#if peek}
		<div class="p-5 sm:p-6">
			<div class="flex justify-between gap-3">
				<p class="eyebrow" class:text-primary-500={!invalid} class:text-ember={invalid}>Friend code</p>
				<p class="eyebrow font-mono">{invalid ? 'Expired' : `${minutes} min left`}</p>
			</div>

			{#if invalid}
				<h2 id="accept-title" class="mt-3 font-display text-xl font-extralight text-balance text-chalk">
					That code ran out
				</h2>
				<p id="accept-body" class="mt-2.5 text-sm leading-relaxed text-fog">
					Codes last 15 minutes. Ask for a new one.
				</p>
				<div class="mt-5 grid grid-cols-2 gap-2">
					{#if account.signedIn}
						<button type="button" class={primary} onclick={onownCode}>Show my code</button>
					{/if}
					<button type="button" class={outline} class:col-span-2={!account.signedIn} onclick={() => dialog?.close()}>
						Done
					</button>
				</div>
			{:else if found && !account.signedIn}
				<h2 id="accept-title" class="mt-3 font-display text-xl font-extralight text-balance text-chalk">
					{found.username} wants to be friends
				</h2>
				<p id="accept-body" class="mt-2.5 text-sm leading-relaxed text-fog">
					Sign in or create an account to accept. The code works for another {minutes}
					{minutes === 1 ? 'minute' : 'minutes'}.
				</p>
				<div class="mt-5 grid grid-cols-2 gap-2">
					<a href={`${resolve('/settings/account')}?next=${back}`} class={primary}>Sign in</a>
					<button type="button" class={outline} onclick={() => dialog?.close()}>Not now</button>
				</div>
				<!-- an app installed on an iPhone keeps its own sign-in, apart from Safari's,
				     so the camera may land here signed out while the app is signed in -->
				<div class="mt-5 border-t border-haze pt-4">
					<p class="text-[0.8125rem] leading-normal text-fog">
						Signed in on the home-screen app instead? Open Settings › Friends there and enter this
						code:
					</p>
					<p class="mt-2 font-mono text-[1.375rem] leading-tight tracking-[0.16em] text-chalk">
						{spaced(code.toUpperCase().replace(/[^0-9A-Z]/g, ''))}
					</p>
				</div>
			{:else if found && own}
				<h2 id="accept-title" class="mt-3 font-display text-xl font-extralight text-balance text-chalk">
					That’s your own code
				</h2>
				<p id="accept-body" class="mt-2.5 text-sm leading-relaxed text-fog">
					Show it to the people you want to add, or send them the link. Everyone who uses it in the
					next 15 minutes becomes your friend.
				</p>
				<div class="mt-5 grid grid-cols-2 gap-2">
					<button type="button" class={primary} onclick={onownCode}>Show my code</button>
					<button type="button" class={outline} onclick={() => dialog?.close()}>Done</button>
				</div>
			{:else if found}
				<h2 id="accept-title" class="mt-3 font-display text-xl font-extralight text-balance text-chalk">
					Add {found.username} as a friend?
				</h2>
				<p id="accept-body" class="mt-2.5 text-sm leading-relaxed text-fog">
					You’ll both see playlists shared with friends, and either of you can let the other edit
					one.
				</p>
				<div class="mt-5 grid grid-cols-2 gap-2">
					<button type="button" class={primary} disabled={busy} onclick={accept}>
						{busy ? 'Adding…' : `Add ${found.username}`}
					</button>
					<button type="button" class={outline} onclick={() => dialog?.close()}>Not now</button>
				</div>
				{#if friends.error}
					<p class="mt-3 text-sm text-ember" role="alert">{friends.error}</p>
				{/if}
			{/if}
		</div>
	{/if}
</dialog>
