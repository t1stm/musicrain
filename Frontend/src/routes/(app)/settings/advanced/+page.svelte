<script lang="ts">
	import { onMount } from 'svelte';
	import Switch from '$components/settings/Switch.svelte';
	import SyncToggle from '$components/settings/SyncToggle.svelte';
	import { haptic } from '$lib/haptics';
	import advanced, { vibrations } from '$states/advanced.svelte';

	// Whether this browser offers vibration at all: feature detection is the only query there
	// is — no permission to request, no way to ask whether a motor exists. Chrome on Android has
	// the API and Safari does not. Desktop Chromium answers yes with no motor to shake, and
	// Android answers only once the page has had a tap — so yes means "can", not "will be
	// felt". Asked once mounted: there is no `navigator` while the page renders on the server.
	// `null` until then.
	let vibrates = $state<boolean | null>(null);
	onMount(() => (vibrates = 'vibrate' in navigator));
</script>

<svelte:head><title>Advanced · Settings · musicrain</title></svelte:head>

<h1 class="font-display text-lg leading-tight font-light tracking-tight text-chalk sm:text-2xl">
	Advanced
</h1>

<section class="mt-6 flex flex-col gap-2">
	<h2 class="eyebrow flex items-center gap-3">
		Track menu
		<span class="h-px flex-1 bg-haze"></span>
	</h2>

	<label class="flex min-h-11 cursor-pointer items-center justify-between gap-4 text-sm text-chalk">
		Show Download raw and Copy ID
		<Switch bind:checked={advanced.trackTools} />
	</label>
	<p class="max-w-lg text-sm text-fog">
		Two tools in every track's menu: the original file, where there is one, and the ID the API
		knows the track by.
	</p>
</section>

<section class="mt-9 flex flex-col gap-3">
	<h2 class="eyebrow flex items-center gap-3">
		Vibration
		<span class="h-px flex-1 bg-haze"></span>
	</h2>

	<p class="max-w-lg text-sm text-fog">
		A tick in the hand when a swipe or a hold has gone far enough to land. Phones' motors
		differ, so pick one you can feel.
	</p>
	<p class="max-w-lg text-sm text-fog">
		Whether anything is felt depends on the browser: Chrome on Android vibrates, Safari on an
		iPhone does not.
		{#if vibrates === true}
			This browser can, on a device with a motor, once you have tapped the page.
		{:else if vibrates === false}
			<span class="text-chalk">This browser can't vibrate</span>, so this setting does nothing
			here.
		{/if}
	</p>
	<fieldset disabled={vibrates === false} class="disabled:opacity-60 [&:disabled_label]:cursor-default">
		<legend class="sr-only">Vibration length</legend>
		<div class="grid grid-cols-4 gap-1 rounded-row border border-haze p-1">
			{#each vibrations as vibration (vibration.ms)}
				<label
					class="flex min-h-11 cursor-pointer items-center justify-center rounded-art text-sm text-fog hover:text-chalk has-checked:bg-primary-600 has-checked:text-white has-focus-visible:outline-2 has-focus-visible:outline-primary-200"
				>
					<input
						type="radio"
						name="vibration"
						value={vibration.ms}
						bind:group={advanced.vibrationMs}
						onchange={haptic}
						class="sr-only"
					/>
					{vibration.label}
				</label>
			{/each}
		</div>
	</fieldset>
	<SyncToggle key="vibrationMs" />
</section>

<section class="mt-9 flex flex-col gap-2">
	<h2 class="eyebrow flex items-center gap-3">
		Room sync
		<span class="h-px flex-1 bg-haze"></span>
	</h2>

	<label class="flex min-h-11 cursor-pointer items-center justify-between gap-4 text-sm text-chalk">
		Log room sync to the browser console
		<Switch bind:checked={advanced.logSync} />
	</label>
	<p class="max-w-lg text-sm text-fog">
		Every sample, seek and correction the room's clock makes, for working out why a room drifts.
	</p>
	<SyncToggle key="logSync" />
</section>
