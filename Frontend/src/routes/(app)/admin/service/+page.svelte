<script lang="ts">
	import { page } from '$app/state';
	import Requests from '$components/admin/Requests.svelte';
	import { viewOf } from '$components/admin/views';
	import Skeleton from '$components/Skeleton.svelte';
	import fleet from '$states/fleet.svelte';

	let name = $derived(page.url.searchParams.get('name') ?? '');
	let service = $derived(fleet.service(name));
	let tab = $state<'state' | 'requests'>('state');
	let View = $derived(viewOf(name));

	const tabClass = 'border-b-2 px-2.5 py-1.5 text-sm outline-none focus-visible:ring-2 focus-visible:ring-primary-500';
</script>

<svelte:head><title>{name} · Admin · musicrain</title></svelte:head>

<header class="flex flex-wrap items-baseline gap-x-3">
	<h1 class="font-display text-lg leading-tight font-light tracking-tight text-chalk sm:text-2xl">{name}</h1>
	{#if service}<span class="font-mono text-xs break-all text-fog">{service.url}</span>{/if}
</header>
{#if service}
	<p class="mt-1 text-xs text-fog">
		<span class={service.up ? 'text-primary-500' : 'text-ember'}>{service.up ? 'up' : 'not answering'}</span>
		· refreshed {new Date(fleet.latest!.at).toLocaleTimeString()}
		{#if !fleet.may(name)}· {fleet.needs(name).replace(/^Needs/, 'acting needs')}{/if}
	</p>
{/if}

<div class="mt-4 mb-4 flex gap-1 border-b border-haze" role="tablist">
	{#each [['state', 'State'], ['requests', 'Requests']] as [value, label] (value)}
		<button
			type="button"
			role="tab"
			aria-selected={tab === value}
			class="{tabClass} {tab === value ? 'border-primary-0 text-chalk' : 'border-transparent text-fog hover:text-chalk'}"
			onclick={() => (tab = value as typeof tab)}>{label}</button
		>
	{/each}
</div>

{#key name}
	{#if tab === 'requests'}
		<Requests target={name} />
	{:else if !fleet.latest}
		<div aria-busy="true" aria-label="Loading">
			<Skeleton class="h-16 w-full max-w-md" />
			<Skeleton class="mt-4 h-40 w-full" />
		</div>
	{:else if !service}
		<p class="text-sm text-fog">Oko watches no target called {name}.</p>
	{:else if !service.up}
		<p class="admin-scroll admin-empty text-ember!">Not answering — {service.error}</p>
	{:else}
		<svelte:boundary>
			<View {name} snapshot={service.snapshot ?? {}} />
			{#snippet failed(error)}
				<p class="admin-scroll admin-empty text-ember!">
					{error instanceof Error ? error.message : 'This view could not read the snapshot.'}
				</p>
			{/snippet}
		</svelte:boundary>
	{/if}
{/key}
