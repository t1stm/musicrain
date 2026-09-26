<script lang="ts">
	import { Beaker, Cloud, FolderOpen, Icon, MagnifyingGlass, RectangleStack, User } from 'svelte-hero-icons';
	import { page } from '$app/state';
	import { resolve } from '$app/paths';
	import user from '$states/user.svelte';

	// The queue/chat sheet is a modal on a phone, and it would sit over the settings page
	// the avatar opens. Following the avatar closes it.
	let { onaccount }: { onaccount?: () => void } = $props();

	const isAlpha = true;
	let searchTerm = $derived(page.url.searchParams.get('term') ?? '');
	let inSettings = $derived(page.url.pathname.startsWith('/settings'));
</script>

<header
	class="box-border flex h-12 w-full shrink-0 justify-center bg-surface-0 px-3 py-1.5 micro:hidden sm:h-14 sm:px-4 sm:py-2"
>
	<div class="flex h-full w-full justify-between gap-2">
		<!-- The mark and the way into the library travel together: one flex child, so
		     justify-between distributes its space around the pair instead of pushing the
		     folder off towards the search field. -->
		<div class="flex shrink-0 items-center gap-2 sm:mr-2">
			<a
				href={resolve('/')}
				class="flex items-center gap-1.5 rounded-row outline-none focus-visible:ring-2 focus-visible:ring-primary-500"
			>
				<span class="hidden font-display text-lg font-extralight tracking-tight text-chalk sm:inline"
					>music<b class="font-medium text-primary-500">rain</b></span
				>
				<Icon src={Cloud} solid class="size-6 shrink-0 text-primary-500" />
				{#if isAlpha}
					<Icon src={Beaker} micro class="mt-auto mb-1 hidden size-3.5 text-fog sm:block" />
				{/if}
			</a>

			<!-- Gold is the library everywhere in this app, so the way into it is gold on hover.
			     On a phone too: the search field gives up the width, and still holds its
			     placeholder at 320px. -->
			<a
				href={resolve('/browse')}
				aria-label="Browse the library by folder"
				class="flex size-9 shrink-0 items-center justify-center rounded-row border border-haze text-fog outline-none hover:border-gold hover:text-gold focus-visible:ring-2 focus-visible:ring-primary-500 sm:size-10"
				class:border-gold={page.url.pathname.startsWith('/browse')}
				class:text-gold={page.url.pathname.startsWith('/browse')}
			>
				<Icon src={FolderOpen} micro size="20" />
			</a>

			<!-- Playlists are not the library — they are what people cut out of it — so this
			     door is the app's own violet, not gold. -->
			<a
				href={resolve('/playlists')}
				aria-label="Playlists"
				class="flex size-9 shrink-0 items-center justify-center rounded-row border border-haze text-fog outline-none hover:border-primary-0 hover:text-primary-500 focus-visible:ring-2 focus-visible:ring-primary-500 sm:size-10"
				class:border-primary-0={page.url.pathname.startsWith('/playlist')}
				class:text-primary-500={page.url.pathname.startsWith('/playlist')}
			>
				<Icon src={RectangleStack} micro size="20" />
			</a>
		</div>

		<form class="flex min-w-0 gap-2 w-full max-w-lg" action="/search">
			<!-- `search` gives a phone's keyboard its Search key and the field its clear button -->
			<input
				type="search"
				enterkeyhint="search"
				name="term"
				bind:value={searchTerm}
				placeholder="Search"
				class="rounded-row border border-haze bg-dark-0 w-full text-chalk placeholder:text-fog ring-primary-0 focus:border-primary-0 focus-visible:ring-2"
			/>
			<!-- the form already submits on Enter; the button is a desktop affordance -->
			<button
				class="hidden size-10 min-w-10 cursor-pointer items-center justify-center rounded-row bg-primary-600 sm:flex"
				type="submit"
			>
				<Icon src={MagnifyingGlass} micro color="white" size="24" />
			</button>
		</form>

		<a
			href={resolve('/settings')}
			aria-label="Settings"
			aria-current={inSettings ? 'page' : undefined}
			class="flex size-9 shrink-0 items-center justify-center overflow-hidden rounded-full bg-primary-600 outline-none focus-visible:ring-2 focus-visible:ring-primary-200 sm:size-10"
			class:ring-2={inSettings}
			class:ring-primary-200={inSettings}
			onclick={() => onaccount?.()}
		>
			{#if user.avatarUrl}
				<!-- a blocked or dead CDN URL drops back to the icon -->
				<img
					src={user.avatarUrl}
					alt=""
					class="size-full object-cover"
					onerror={() => (user.avatarUrl = null)}
				/>
			{:else}
				<Icon src={User} micro size="24" color="white" />
			{/if}
		</a>
	</div>
</header>
