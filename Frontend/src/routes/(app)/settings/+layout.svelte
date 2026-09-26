<script lang="ts">
	import { ChevronLeft, ChevronRight, Icon } from 'svelte-hero-icons';
	import { afterNavigate } from '$app/navigation';
	import { page } from '$app/state';
	import { resolve } from '$app/paths';
	import account from '$states/account.svelte';
	import quality from '$states/quality.svelte';
	import user from '$states/user.svelte';

	let { children } = $props();

	// A phone gets the list, then one category at a time. Wider, the list is a rail beside
	// the open category. Each row says where it stands, so the list alone answers "what is
	// it set to" without opening anything.
	const categories = [
		{
			href: resolve('/settings/account'),
			label: 'Account',
			summary: () => account.username ?? 'Signed out'
		},
		{
			href: resolve('/settings/playback'),
			label: 'Playback',
			summary: () =>
				quality.codec === 'FLAC' ? 'FLAC' : `${quality.codec} · ${quality.bitrate}`
		},
		{
			href: resolve('/settings/rooms'),
			label: 'Rooms',
			summary: () => user.username || 'Anonymous'
		},
		{ href: resolve('/settings/advanced'), label: 'Advanced', summary: () => '' }
	];

	let index = $derived(page.route.id === '/(app)/settings');

	// Back to the list is a step back when the list is where we came from. A link would push
	// the list on top instead, and the back gesture would then land on the category again.
	let fromIndex = false;
	afterNavigate(({ from }) => (fromIndex = from?.route.id === '/(app)/settings'));

	function toList(event: MouseEvent) {
		if (!fromIndex) return;
		event.preventDefault();
		history.back();
	}
</script>

<div class="page p-4 sm:p-6 sm:pb-28">
	<div class="mx-auto flex w-full max-w-4xl gap-10">
		<nav aria-label="Settings" class="w-full sm:w-44 sm:shrink-0" class:max-sm:hidden={!index}>
			<svelte:element
				this={index ? 'h1' : 'p'}
				class="font-display text-lg leading-tight font-light tracking-tight text-chalk sm:text-2xl"
			>
				Settings
			</svelte:element>
			<ul class="mt-4 flex flex-col border-t border-haze sm:mt-6 sm:border-t-0">
				{#each categories as category (category.href)}
					{@const open = page.url.pathname === category.href}
					<li class="border-b border-haze sm:border-b-0">
						<a
							href={category.href}
							aria-current={open ? 'page' : undefined}
							class="flex min-h-12 items-center gap-3 rounded-row px-1 text-chalk outline-none focus-visible:ring-2 focus-visible:ring-primary-500 sm:min-h-10 sm:px-3 sm:text-sm sm:text-fog sm:hover:bg-surface-100 sm:hover:text-chalk"
							class:sm:bg-surface-100={open}
							class:sm:text-chalk={open}
						>
							{category.label}
							<span class="ml-auto min-w-0 truncate font-mono text-xs text-fog sm:hidden"
								>{category.summary()}</span
							>
							<Icon src={ChevronRight} micro class="size-4 shrink-0 text-fog sm:hidden" />
						</a>
					</li>
				{/each}
			</ul>
		</nav>

		{#if index}
			<!-- draws nothing; it is where a wide screen is sent on to the first category -->
			{@render children()}
		{:else}
			<div class="min-w-0 flex-1">
				<a
					href={resolve('/settings')}
					onclick={toList}
					class="-ml-1 mb-3 inline-flex min-h-11 items-center gap-1 rounded-row pr-2 text-sm text-fog outline-none hover:text-chalk focus-visible:ring-2 focus-visible:ring-primary-500 sm:hidden"
				>
					<Icon src={ChevronLeft} micro class="size-4" />
					Settings
				</a>
				{@render children()}
			</div>
		{/if}
	</div>
</div>
