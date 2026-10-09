<script lang="ts">
	import Switch from '$components/settings/Switch.svelte';
	import SyncToggle from '$components/settings/SyncToggle.svelte';
	import { clearHistory } from '$requests/history';
	import { AudioApiError } from '$requests/songs';
	import account from '$states/account.svelte';
	import history from '$states/history.svelte';
	import lyrics from '$states/lyrics.svelte';
	import quality, { bitrates, codecs } from '$states/quality.svelte';

	/** Bars rise with log(kbps): 32 is a fifth of the ladder, 320 the whole of it. */
	const height = (kbps: number) => 20 + (80 * Math.log(kbps / 32)) / Math.log(10);

	/** kbps × 3600 s ÷ 8 bits ÷ 1000 — on a phone, this is what "quality" costs. */
	let perHour = $derived(Math.round(quality.bitrate * 0.45));

	// Clearing asks once, and the question says how far it reaches: signed in, the account's
	// history from every device; signed out, only what this device recorded.
	let confirming = $state(false);
	let clearing = $state(false);
	let cleared = $state('');

	async function clear() {
		clearing = true;
		try {
			const { deleted } = await clearHistory(account.token);
			cleared = deleted === 1 ? 'Cleared 1 play.' : `Cleared ${deleted} plays.`;
			confirming = false;
		} catch (error) {
			if (error instanceof AudioApiError) account.reject(error.status);
			cleared = 'Could not reach the history service. Try again shortly.';
		} finally {
			clearing = false;
		}
	}
</script>

<svelte:head><title>Playback · Settings · musicrain</title></svelte:head>

<h1 class="font-display text-lg leading-tight font-light tracking-tight text-chalk sm:text-2xl">
	Playback
</h1>

<section class="mt-6 flex flex-col gap-4">
	<h2 class="eyebrow flex items-center gap-3">
		Streaming quality
		<span class="h-px flex-1 bg-haze"></span>
	</h2>

	<fieldset>
		<legend class="mb-2 text-sm text-chalk">Format</legend>
		<div class="grid grid-cols-5 gap-1 rounded-row border border-haze p-1">
			{#each codecs as codec (codec)}
				<label
					class="relative flex min-h-11 cursor-pointer items-center justify-center rounded-art text-sm text-fog hover:text-chalk has-checked:bg-primary-600 has-checked:text-white has-focus-visible:outline-2 has-focus-visible:outline-primary-200 sm:min-h-9"
				>
					<input type="radio" name="codec" value={codec} bind:group={quality.codec} class="sr-only" />
					{codec}
				</label>
			{/each}
		</div>
	</fieldset>

	{#if quality.codec === 'FLAC'}
		<p class="text-sm text-fog">Lossless. Size follows the file.</p>
	{:else}
		<!-- The ladder: a level meter where every bar up to the chosen rate is lit. Underneath
		     it is a native radio group, so arrow keys and screen readers get it for free. -->
		<fieldset>
			<legend class="mb-2 text-sm text-chalk">Bitrate</legend>
			<div class="flex h-20 items-end gap-1">
				{#each bitrates as kbps (kbps)}
					<label class="flex h-full flex-1 cursor-pointer items-end" title="{kbps} kbps">
						<input
							type="radio"
							name="bitrate"
							value={kbps}
							bind:group={quality.bitrate}
							aria-label="{kbps} kbps"
							class="peer sr-only"
						/>
						<span
							class="w-full rounded-t-art transition-colors duration-120 peer-focus-visible:outline-2 peer-focus-visible:outline-offset-2 peer-focus-visible:outline-primary-200 motion-reduce:transition-none"
							class:bg-primary-600={kbps <= quality.bitrate}
							class:bg-surface-300={kbps > quality.bitrate}
							style:height="{height(kbps)}%"
						></span>
					</label>
				{/each}
			</div>
			<div class="mt-1 flex justify-between font-mono text-[0.68rem] text-fog" aria-hidden="true">
				<span>{bitrates[0]}</span><span>{bitrates[bitrates.length - 1]}</span>
			</div>
		</fieldset>
		<p class="font-mono text-sm text-fog">
			<span class="text-chalk">{quality.bitrate} kbps</span> · ≈ {perHour} MB an hour
		</p>
	{/if}

	<SyncToggle key="quality" />
</section>

<section class="mt-9 flex flex-col gap-4">
	<h2 class="eyebrow flex items-center gap-3">
		Lyrics
		<span class="h-px flex-1 bg-haze"></span>
	</h2>

	<label class="flex min-h-11 cursor-pointer items-center justify-between gap-4 text-sm text-chalk">
		Show lyrics in the full player
		<Switch bind:checked={lyrics.open} />
	</label>
</section>

<section class="mt-9 flex flex-col gap-4">
	<h2 class="eyebrow flex items-center gap-3">
		Listening history
		<span class="h-px flex-1 bg-haze"></span>
	</h2>

	<label class="flex min-h-11 cursor-pointer items-center justify-between gap-4 text-sm text-chalk">
		Record what you play
		<Switch bind:checked={() => !history.paused, (on) => (history.paused = !on)} />
	</label>
	<p class="-mt-2 text-sm text-fog">
		{account.signedIn
			? 'On every device signed in to your account.'
			: 'On this device. Sign in and what it recorded moves to your account.'}
	</p>

	{#if confirming}
		<div class="flex flex-col gap-2 rounded-row border border-haze p-3">
			<p class="text-sm text-chalk">
				{account.signedIn
					? 'Clear every play on your account, from every device? This can\'t be undone.'
					: 'Clear every play this device recorded? This can\'t be undone.'}
			</p>
			<div class="flex flex-wrap gap-2">
				<button
					type="button"
					disabled={clearing}
					class="min-h-11 rounded-row bg-ember px-4 text-sm font-semibold text-white hover:brightness-110 disabled:opacity-60 sm:min-h-9"
					onclick={clear}
				>
					Clear history
				</button>
				<button
					type="button"
					class="min-h-11 rounded-row border border-haze px-4 text-sm font-semibold text-chalk hover:bg-surface-200 sm:min-h-9"
					onclick={() => (confirming = false)}
				>
					Keep it
				</button>
			</div>
		</div>
	{:else}
		<button
			type="button"
			class="min-h-11 w-fit rounded-row border border-haze px-4 text-sm font-semibold text-ember hover:bg-surface-200 sm:min-h-9"
			onclick={() => ((confirming = true), (cleared = ''))}
		>
			Clear history…
		</button>
	{/if}
	{#if cleared}<p class="text-sm text-fog" aria-live="polite">{cleared}</p>{/if}
</section>
