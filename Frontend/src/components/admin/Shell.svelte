<script lang="ts">
	import type { Snippet } from 'svelte';
	import { ChevronLeft, ChevronRight, Icon } from 'svelte-hero-icons';
	import { afterNavigate } from '$app/navigation';
	import { page } from '$app/state';
	import { resolve } from '$app/paths';
	import Feed from './Feed.svelte';
	import { headline } from './views';
	import account from '$states/account.svelte';
	import fleet from '$states/fleet.svelte';

	/**
	 * Everything inside the staff gate: the rail, the live feed strip, and the poll. Mounted only
	 * once the gate passes, so nothing is polled for an account that is not staff.
	 * The settings section's shape: on a phone the list first, then one page at a time; wider, the
	 * list is a rail beside the open page.
	 */
	let { children }: { children: Snippet } = $props();

	$effect(() => fleet.start());

	let index = $derived(page.route.id === '/(app)/admin');
	let live = $derived(page.route.id === '/(app)/admin/live');
	let open = $derived(page.route.id === '/(app)/admin/service' ? page.url.searchParams.get('name') : null);

	const pages = $derived([
		{ href: resolve('/admin'), label: 'Overview', wide: true },
		{ href: resolve('/admin/live'), label: 'Live activity' },
		{ href: resolve('/admin/audit'), label: 'Audit' },
		...(account.owner ? [{ href: resolve('/admin/staff'), label: 'Staff' }] : []),
		...(account.can('studio') ? [{ href: resolve('/studio'), label: 'Lyrics studio' }] : [])
	]);

	const serviceHref = (name: string) => `${resolve('/admin/service')}?name=${encodeURIComponent(name)}`;

	// back to the list is a step back when the list is where we came from, as in settings
	let fromIndex = false;
	afterNavigate(({ from }) => (fromIndex = from?.route.id === '/(app)/admin'));

	function toList(event: MouseEvent) {
		if (!fromIndex) return;
		event.preventDefault();
		history.back();
	}

	const row =
		'flex min-h-12 items-center gap-3 rounded-row px-1 text-chalk outline-none focus-visible:ring-2 focus-visible:ring-primary-500 sm:min-h-9 sm:px-3 sm:text-sm sm:text-fog sm:hover:bg-surface-100 sm:hover:text-chalk';
</script>

<div class="flex gap-10">
	<nav aria-label="Admin" class="w-full sm:w-48 sm:shrink-0" class:max-sm:hidden={!index}>
		<svelte:element
			this={index ? 'h1' : 'p'}
			class="font-display text-lg leading-tight font-light tracking-tight text-chalk sm:text-2xl"
		>
			Admin
		</svelte:element>
		<ul class="mt-4 flex flex-col border-t border-haze sm:mt-6 sm:border-t-0">
			{#each pages as entry (entry.href)}
				{@const current = page.url.pathname === entry.href}
				<li class="border-b border-haze sm:border-b-0" class:max-sm:hidden={entry.wide}>
					<a
						href={entry.href}
						aria-current={current ? 'page' : undefined}
						class={row}
						class:sm:bg-surface-100={current}
						class:sm:text-chalk={current}
					>
						{entry.label}
						<Icon src={ChevronRight} micro class="ml-auto size-4 shrink-0 text-fog sm:hidden" />
					</a>
				</li>
			{/each}
		</ul>

		<h2 class="eyebrow mt-6 mb-1 sm:px-3">Services</h2>
		<ul class="flex flex-col border-t border-haze sm:border-t-0">
			{#each fleet.latest?.services ?? [] as service (service.name)}
				{@const current = open === service.name}
				<li class="border-b border-haze sm:border-b-0">
					<a
						href={serviceHref(service.name)}
						aria-current={current ? 'page' : undefined}
						class={row}
						class:sm:bg-surface-100={current}
						class:sm:text-chalk={current}
					>
						<span
							class="size-1.5 shrink-0 rounded-full {service.up ? 'bg-primary-0' : 'bg-ember'}"
							title={service.up ? 'up' : 'not answering'}
						></span>
						<span class="truncate">{service.name}</span>
						<span class="ml-auto min-w-0 truncate font-mono text-xs text-fog sm:hidden"
							>{service.up ? headline(service.name, service.snapshot) : 'down'}</span
						>
						<Icon src={ChevronRight} micro class="size-4 shrink-0 text-fog sm:hidden" />
					</a>
				</li>
			{:else}
				<li class="py-2 text-sm text-fog sm:px-3">{fleet.error || 'Loading…'}</li>
			{/each}
		</ul>
	</nav>

	<div class="min-w-0 flex-1" class:max-sm:hidden={index}>
		{#if !index}
			<a
				href={resolve('/admin')}
				onclick={toList}
				class="-ml-1 mb-3 inline-flex min-h-11 items-center gap-1 rounded-row pr-2 text-sm text-fog outline-none hover:text-chalk focus-visible:ring-2 focus-visible:ring-primary-500 sm:hidden"
			>
				<Icon src={ChevronLeft} micro class="size-4" />
				Admin
			</a>
		{/if}

		{#if !live}
			<!-- the newest three lines of the feed, on every page but the one that shows all of it -->
			<a
				href={resolve('/admin/live')}
				class="mb-5 block overflow-hidden rounded-panel border border-haze bg-surface-0 py-1.5 outline-none hover:border-surface-300 focus-visible:ring-2 focus-visible:ring-primary-500"
			>
				<span class="eyebrow block px-3 pb-0.5"
					>Live activity{#if !fleet.following}<span class="normal-case"> — reconnecting</span>{/if}</span
				>
				<div class="h-[3.75rem]"><Feed limit={3} /></div>
			</a>
		{/if}

		{@render children()}
	</div>
</div>
