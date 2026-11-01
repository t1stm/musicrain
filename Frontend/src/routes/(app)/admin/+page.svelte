<script lang="ts">
	import { resolve } from '$app/paths';
	import { headline } from '$components/admin/views';
	import account from '$states/account.svelte';
	import fleet from '$states/fleet.svelte';

	const serviceHref = (name: string) => `${resolve('/admin/service')}?name=${encodeURIComponent(name)}`;
</script>

<svelte:head><title>Admin · musicrain</title></svelte:head>

<!-- wide screens only: on a phone the list beside this is the page, and says the same per row -->
<section>
	<h2 class="font-display text-lg leading-tight font-light tracking-tight text-chalk sm:text-2xl">Overview</h2>
	<p class="mt-2 text-sm text-fog">
		Signed in as <b class="font-semibold text-chalk">{account.username}</b>,
		{#if account.owner}
			the owner: you can change everything.
		{:else if account.permissions.length}
			an admin. You can change what
			{#each account.permissions as permission, i (permission)}{i ? ', ' : ''}<b
					class="font-mono text-xs font-normal text-chalk">{permission}</b
				>{/each} covers, and look at everything else.
		{:else}
			an admin who can look. Ask the owner for what you need to change.
		{/if}
	</p>

	{#if fleet.error}<p class="mt-4 text-sm text-ember" role="alert">{fleet.error}</p>{/if}

	<ul class="mt-6 grid gap-2.5 sm:grid-cols-2 xl:grid-cols-3">
		{#each fleet.latest?.services ?? [] as service (service.name)}
			<li>
				<a
					href={serviceHref(service.name)}
					class="flex h-full flex-col gap-1 rounded-panel border border-haze bg-surface-0 p-3.5 outline-none hover:border-surface-300 focus-visible:ring-2 focus-visible:ring-primary-500"
				>
					<span class="flex items-center gap-2 text-[0.9375rem] text-chalk">
						<span class="size-1.5 rounded-full {service.up ? 'bg-primary-0' : 'bg-ember'}"></span>
						{service.name}
						<span class="ml-auto font-mono text-xs {service.permission && account.can(service.permission) ? 'text-primary-500' : 'text-fog'}"
							>{service.permission ?? ''}</span
						>
					</span>
					{#if service.up}
						<span class="font-mono text-xs text-fog">{headline(service.name, service.snapshot) || 'up'}</span>
					{:else}
						<span class="text-xs text-ember">Not answering — {service.error}</span>
					{/if}
				</a>
			</li>
		{/each}
	</ul>
</section>
