<script lang="ts">
	import { Cloud, DevicePhoneMobile, Icon } from 'svelte-hero-icons';
	import account from '$states/account.svelte';
	import settings, { type SettingKey } from '$states/settings.svelte';

	// Where the value lives, and the one action that moves it. The cloud is the app's own
	// mark, so a setting that travels with the account wears it. A setting that always
	// travels says nothing; one that never can says so, and offers nothing.
	let { key }: { key: SettingKey } = $props();

	let local = $derived(settings.isDeviceOnly(key));
</script>

{#if settings.syncOf(key) === 'never'}
	<p class="mt-3 flex items-center gap-1.5 text-xs text-fog">
		<Icon src={DevicePhoneMobile} micro class="size-4 shrink-0" />
		Kept on this device
	</p>
{:else if settings.syncOf(key) === 'optional' && account.signedIn}
	<div class="mt-3 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-fog">
		<span class="flex items-center gap-1.5">
			<Icon src={local ? DevicePhoneMobile : Cloud} micro class="size-4 shrink-0 {local ? '' : 'text-primary-500'}" />
			{local ? 'Kept on this device' : 'Synced to your account'}
		</span>
		<button
			type="button"
			class="min-h-11 rounded-row px-1 font-semibold text-primary-500 underline-offset-4 hover:underline focus-visible:outline-2 focus-visible:outline-primary-200 sm:min-h-0"
			onclick={() => settings.setDeviceOnly(key, !local)}
		>
			{local ? 'Sync it again' : 'Keep on this device only'}
		</button>
	</div>
{/if}
