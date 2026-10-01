<script lang="ts">
	// ponytail: a static import, not `import()`. Only this component uses it, and only the
	// friends page imports this component, so it already lands in that route's chunk alone.
	import QRCode from 'qrcode';
	import WaterClock from './WaterClock.svelte';
	import { closeOnBack } from '$lib/backWatcher.svelte';
	import friends, {
		inviteLink,
		minutesAndSeconds,
		secondsLeft,
		spaced
	} from '$states/friends.svelte';

	/**
	 * Your friend code, held up for a group to scan. Everybody who uses it in its fifteen
	 * minutes becomes a friend, so it stays up and lists who has joined; "Done" only closes
	 * this, because people who scanned it may still be making their accounts.
	 */
	let { open = $bindable(false) }: { open?: boolean } = $props();

	let dialog = $state<HTMLDialogElement>();
	let now = $state(Date.now());
	let qr = $state('');
	let copied = $state(false);

	const canShare = typeof navigator !== 'undefined' && 'share' in navigator;

	let invite = $derived(friends.invite);
	let left = $derived(invite ? secondsLeft(invite.expiresUtc, now) : 0);
	let expired = $derived(!!invite && left <= 0);
	let ended = $derived(friends.ended && !expired);
	let done = $derived(expired || ended);
	let link = $derived(invite ? inviteLink(invite.code) : '');

	$effect(() => {
		if (open && dialog && !dialog.open) dialog.showModal();
		if (!open && dialog?.open) dialog.close();
	});

	closeOnBack(
		() => open,
		() => (open = false)
	);

	// The countdown is read from the clock rather than counted down, because a phone
	// throttles a backgrounded tab's timers; coming back re-reads it at once.
	$effect(() => {
		if (!open) return;
		now = Date.now();

		// stops once the code is done, so an ended code keeps saying how much was left
		const tick = () => {
			if (!done) now = Date.now();
		};
		const clock = setInterval(tick, 1000);
		// who has joined, while anybody could still be looking at it
		const poll = setInterval(() => {
			if (document.visibilityState === 'visible' && !done) friends.pollInvite();
		}, 5000);
		document.addEventListener('visibilitychange', tick);

		return () => {
			clearInterval(clock);
			clearInterval(poll);
			document.removeEventListener('visibilitychange', tick);
		};
	});

	// The screen stays on while the code is up: a phone dimming mid-scan is the whole flow
	// failing. The browser lets go of the lock whenever the page is hidden, so it is taken
	// again on the way back. Without the API the page works the same, just dimmer.
	$effect(() => {
		if (!open) return;

		let lock: WakeLockSentinel | null = null;
		const hold = async () => {
			if (document.visibilityState !== 'visible') return;
			try {
				lock = (await navigator.wakeLock?.request('screen')) ?? null;
			} catch {
				// denied, or a battery saver: dimmer, not broken
			}
		};

		hold();
		document.addEventListener('visibilitychange', hold);

		return () => {
			document.removeEventListener('visibilitychange', hold);
			lock?.release().catch(() => undefined);
		};
	});

	// Dark on light, never inverted — plenty of scanners cannot read a light code on dark.
	$effect(() => {
		if (!link) return;

		let live = true;
		QRCode.toString(link, {
			type: 'svg',
			errorCorrectionLevel: 'M',
			margin: 4,
			color: { dark: '#06060d', light: '#e9e6f2' }
		}).then((svg) => {
			if (live) qr = `data:image/svg+xml,${encodeURIComponent(svg)}`;
		});

		return () => {
			live = false;
		};
	});

	/** A phone's share sheet where there is one; the clipboard everywhere else. */
	async function share() {
		try {
			if (canShare) await navigator.share({ url: link });
			else {
				await navigator.clipboard.writeText(link);
				copied = true;
			}
		} catch {
			// the sheet dismissed, or the clipboard blocked (the Discord activity): the code
			// on screen still works, read out loud
		}
	}

	async function newCode() {
		copied = false;
		await friends.openInvite();
		now = Date.now();
	}

	const primary =
		'min-h-11 rounded-row bg-primary-600 px-4 text-sm font-semibold text-white hover:bg-primary-0 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-200';
	const outline =
		'min-h-11 rounded-row border border-haze px-4 text-sm font-semibold text-chalk hover:bg-surface-200';
</script>

<dialog
	bind:this={dialog}
	class="gate"
	closedby="any"
	aria-labelledby="invite-title"
	onclose={() => (open = false)}
>
	<WaterClock {left} spent={done} />

	{#if invite}
		<div class="p-5 sm:p-6">
			<div class="flex justify-between gap-3">
				<p class="eyebrow" class:text-primary-500={!done} class:text-ember={done}>
					{ended ? 'Ended' : expired ? 'Expired' : 'Add friends'}
				</p>
				<p class="eyebrow font-mono">
					{ended ? `${minutesAndSeconds(left)} was left` : `${minutesAndSeconds(left)} left`}
				</p>
			</div>
			<h2 id="invite-title" class="mt-3 font-display text-xl font-extralight text-balance text-chalk">
				{ended ? 'You ended this code' : expired ? 'This code ran out' : 'Scan with a phone camera'}
			</h2>

			{#if qr}
				<img
					src={qr}
					alt="QR code for your friend link"
					class="mx-auto mt-4 block aspect-square w-full max-w-58 rounded-row transition-opacity duration-400 motion-reduce:transition-none"
					class:opacity-12={done}
				/>
			{:else}
				<div class="mx-auto mt-4 aspect-square w-full max-w-58 rounded-row bg-surface-200"></div>
			{/if}

			<p
				class="mt-3 text-center font-mono text-[1.625rem] leading-tight tracking-[0.16em]"
				class:text-chalk={!done}
				class:text-surface-400={done}
				class:line-through={done}
			>
				{spaced(invite.code)}
			</p>
			<p class="mt-1 text-center text-sm text-balance text-fog">
				{#if ended}
					Nobody else can use it. Everyone who joined stays a friend.
				{:else if expired}
					Codes last 15 minutes. Make a new one to add more people.
				{:else}
					Everyone who scans it becomes your friend.
				{/if}
			</p>

			{#if invite.joined.length > 0}
				<div class="mt-3.5 border-t border-haze pt-3">
					<p class="eyebrow">Joined · {invite.joined.length}</p>
					<ul class="mt-2 flex flex-wrap gap-x-3.5 gap-y-2" aria-live="polite">
						{#each invite.joined as name (name)}
							<li class="flex items-center gap-1.5 text-sm text-chalk">
								<!-- the app's one motion idea: somebody lands, and the ring is the ripple -->
								<span
									aria-hidden="true"
									class="grid size-6 place-items-center rounded-full bg-surface-200 text-[0.7rem] font-semibold text-primary-500 animate-ripple motion-reduce:animate-none"
									>{name[0]}</span
								>{name}
							</li>
						{/each}
					</ul>
				</div>
			{/if}

			<div class="mt-4 grid grid-cols-2 gap-2">
				{#if done}
					<button type="button" class={primary} onclick={newCode}>New code</button>
				{:else}
					<button type="button" class={primary} onclick={share}>
						{copied ? 'Copied' : canShare ? 'Share link' : 'Copy link'}
					</button>
				{/if}
				<button type="button" class={outline} onclick={() => (open = false)}>Done</button>
			</div>
			{#if !done}
				<button
					type="button"
					class="mx-auto mt-1 block min-h-11 rounded-row px-3 text-sm text-fog hover:text-chalk"
					onclick={() => friends.endInvite()}
				>
					End code
				</button>
			{/if}
			{#if friends.error}
				<p class="mt-2 text-center text-sm text-ember" role="alert">{friends.error}</p>
			{/if}
		</div>
	{/if}
</dialog>
