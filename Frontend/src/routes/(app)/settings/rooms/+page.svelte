<script lang="ts">
	import { Icon, DevicePhoneMobile, Minus, Plus } from 'svelte-hero-icons';
	import SyncToggle from '$components/settings/SyncToggle.svelte';
	import audio from '$states/audio.svelte';
	import session from '$states/session.svelte';
	import user from '$states/user.svelte';

	// follows the saved name — which the account can still change once its settings arrive —
	// until someone types
	let draftName = $derived(user.username ?? '');
	let saved = $state(false);

	function saveName(event: SubmitEvent) {
		event.preventDefault();
		user.choose(draftName);
		saved = true;
	}

	const step = (by: number) => (audio.latencyMs += by);
</script>

<svelte:head><title>Rooms · Settings · musicrain</title></svelte:head>

<h1 class="font-display text-lg leading-tight font-light tracking-tight text-chalk sm:text-2xl">
	Rooms
</h1>

<section class="mt-6 flex flex-col gap-3">
	<h2 class="eyebrow flex items-center gap-3">
		Chat name
		<span class="h-px flex-1 bg-haze"></span>
	</h2>

	{#if user.source === 'discord'}
		<p class="text-sm text-chalk">{user.username}</p>
		<p class="text-sm text-fog">Discord sets your name inside the activity.</p>
	{:else}
		<form class="flex flex-col gap-3 sm:flex-row" onsubmit={saveName}>
			<!-- `nickname`, so a password manager does not take it for an account username -->
			<input
				type="text"
				name="nickname"
				value={draftName}
				oninput={(event) => {
					draftName = event.currentTarget.value;
					saved = false;
				}}
				maxlength="60"
				autocomplete="nickname"
				placeholder="Anonymous"
				aria-label="Chat name"
				class="min-h-11 w-full rounded-row border border-haze bg-dark-0 text-sm text-chalk placeholder:text-fog ring-primary-0 focus:border-primary-0 focus-visible:ring-2 sm:max-w-xs"
			/>
			<button
				type="submit"
				disabled={draftName.trim() === (user.username ?? '')}
				class="min-h-11 rounded-row bg-primary-600 px-4 text-sm font-semibold text-white hover:bg-primary-0 disabled:opacity-60 sm:min-h-0"
			>
				Save chat name
			</button>
		</form>
		<p class="text-sm text-fog" aria-live="polite">
			{#if saved}
				Chat name saved.
			{/if}
			Everyone in a room sees it on your messages.
			{#if session.inRoom}Renaming reconnects you. The room sees you leave and come back.{/if}
		</p>
		<SyncToggle key="chatName" />
	{/if}
</section>

<section class="mt-9 flex flex-col gap-3">
	<h2 class="eyebrow flex items-center gap-3">
		Output delay
		<span class="h-px flex-1 bg-haze"></span>
	</h2>

	<p class="max-w-lg text-sm text-fog">
		Bluetooth headphones and speakers play late, and the browser cannot always tell. If you hear
		the room behind everyone else, raise this.
	</p>

	<div class="flex items-center gap-2">
		<button
			type="button"
			aria-label="10 milliseconds less"
			class="flex size-11 items-center justify-center rounded-row border border-haze text-fog hover:text-chalk focus-visible:outline-2 focus-visible:outline-primary-200"
			onclick={() => step(-10)}
		>
			<Icon src={Minus} micro class="size-4" />
		</button>
		<label class="flex items-center gap-2 font-mono text-sm text-fog">
			<input
				type="number"
				step="10"
				min="-1000"
				max="1000"
				bind:value={audio.latencyMs}
				aria-label="Output delay adjustment, milliseconds"
				class="min-h-11 w-24 rounded-row border border-haze bg-dark-0 text-right font-mono text-chalk ring-primary-0 focus:border-primary-0 focus-visible:ring-2"
			/>
			<span aria-hidden="true">ms</span>
		</label>
		<button
			type="button"
			aria-label="10 milliseconds more"
			class="flex size-11 items-center justify-center rounded-row border border-haze text-fog hover:text-chalk focus-visible:outline-2 focus-visible:outline-primary-200"
			onclick={() => step(10)}
		>
			<Icon src={Plus} micro class="size-4" />
		</button>
	</div>

	<p class="font-mono text-xs text-fog">
		{#if audio.measuredMs > 0}
			The browser measures {audio.measuredMs} ms. With this, {audio.measuredMs + audio.latencyMs} ms
			in all.
		{:else}
			The browser has not measured this device yet. It does once something plays.
		{/if}
	</p>

	<!-- the delay is this device's output path, so it never goes to the account -->
	<p class="flex items-center gap-1.5 text-xs text-fog">
		<Icon src={DevicePhoneMobile} micro class="size-4 shrink-0" />
		Kept on this device
	</p>
</section>
