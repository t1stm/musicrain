<script lang="ts">
	import { ChevronRight, Icon } from 'svelte-hero-icons';
	import { resolve } from '$app/paths';
	import account from '$states/account.svelte';
</script>

<svelte:head><title>Admin · musicrain</title></svelte:head>

<h1 class="font-display text-lg leading-tight font-light tracking-tight text-chalk sm:text-2xl">
	Admin
</h1>
<p class="mt-2 text-sm text-fog">
	Signed in as <b class="font-semibold text-chalk">{account.username}</b>, {account.owner
		? 'the owner'
		: 'an admin'}.
</p>

<section class="mt-7 max-w-md">
	<h2 class="eyebrow flex items-center gap-3">
		What you can change
		<span class="h-px flex-1 bg-haze"></span>
	</h2>
	{#if account.owner}
		<p class="mt-3 text-sm text-chalk">Everything, and who else is on the staff.</p>
	{:else if account.permissions.length}
		<ul class="mt-3 flex flex-wrap gap-1.5">
			{#each account.permissions as permission (permission)}
				<li
					class="rounded-art border border-primary-0 bg-primary-600 px-2 py-1 font-mono text-xs text-white"
				>
					{permission}
				</li>
			{/each}
		</ul>
	{:else}
		<p class="mt-3 text-sm text-fog">Nothing yet. Ask the owner for what you need.</p>
	{/if}
</section>

{#if account.owner}
	<a
		href={resolve('/admin/staff')}
		class="mt-7 flex min-h-15 max-w-md items-center gap-3 border-y border-haze px-1 outline-none hover:bg-surface-100 focus-visible:ring-2 focus-visible:ring-primary-500"
	>
		<span class="min-w-0 flex-1">
			<span class="block text-[0.9375rem] leading-snug text-chalk">Staff</span>
			<span class="block text-[0.8125rem] leading-snug text-fog">Choose who can change what</span>
		</span>
		<Icon src={ChevronRight} micro class="size-4 shrink-0 text-fog" />
	</a>
{/if}
