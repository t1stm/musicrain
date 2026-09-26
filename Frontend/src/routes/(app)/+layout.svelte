<script lang="ts">
	// self-hosted so they survive the Discord activity's CSP (no external font hosts)
	import '@fontsource-variable/unbounded/wght.css';
	import '@fontsource-variable/golos-text/wght.css';
	import '@fontsource-variable/jetbrains-mono/wght.css';
	import '../../app.css';
	import { onMount } from 'svelte';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { resolve } from '$app/paths';
	import { discordIds, discordUser, initDiscord } from '$lib/discord';
	import { closeOnBack, watchBackNavigation } from '$lib/backWatcher.svelte';
	import { swipe } from '$lib/swipe';
	import Header from '$components/header/Header.svelte';
	import Player from '$components/player/Player.svelte';
	import Queue from '$components/queue/Queue.svelte';
	import Chat from '$components/chat/Chat.svelte';
	import SessionStrip from '$components/session/SessionStrip.svelte';
	import account from '$states/account.svelte';
	import queue from '$states/queue.svelte';
	import rooms from '$states/rooms.svelte';
	import session from '$states/session.svelte';
	import user from '$states/user.svelte';

	let { children } = $props();
	let dock = $state<'queue' | 'chat' | null>(null);

	// The app's one back handler: everything opened on top of the page closes before back
	// leaves it. The sheet is a layer like any other — on a phone it is most of the screen.
	watchBackNavigation();
	closeOnBack(() => dock !== null, () => (dock = null));

	$effect(() => {
		session.chatOpen = dock === 'chat';
		if (dock === 'chat') session.unread = 0;
	});

	// a rename confirms to the sender only, so the room's title reaches everybody
	// else through the lobby feed — keep it open for the whole session, not just
	// while the room route is mounted
	$effect(() => {
		if (!session.inRoom) return;
		rooms.connect();
		return () => rooms.disconnect();
	});

	onMount(async () => {
		user.load();
		account.load();
		// clears Discord's activity loading screen; a no-op in a normal browser tab
		await initDiscord();
		// skips the "pick a name" gate on the room page and fills the header avatar
		if (discordUser) user.adopt(discordUser.name, discordUser.avatarUrl);

		const ids = discordIds();
		if (!ids || page.url.pathname.startsWith('/room')) return;

		// the voice channel you launched from is the room — never make anyone pick
		// it out of a list
		rooms.connect();
		await Promise.race([rooms.ready, new Promise((done) => setTimeout(done, 4000))]);
		try {
			// The channel is the room, not the launch: Discord mints a fresh
			// `instanceId` every time the activity is started, so keying on it made a
			// new room per launch instead of rejoining the channel's own.
			// ponytail: the channel's real name needs sdk.commands.authenticate(),
			// which needs a token endpoint the API does not have yet. Rename in-session.
			const key = ids.channelId ?? ids.instanceId;
			const roomId = await rooms.findOrCreateForDiscord(`discord:${key}`, 'Discord activity');
			await goto(`${resolve('/room')}?id=${roomId}`);
		} finally {
			rooms.disconnect();
		}
	});
</script>

<div class="relative flex h-svh w-full flex-col overflow-hidden">
	<!-- micro hides the page rather than unmounting it: the route component owns
	     the room's connect effect, so tearing it down would drop the listener out
	     of the room the player is still playing. -->
	<Header />
	{#if session.inRoom}
		<SessionStrip />
	{/if}
	<!-- Page and dock share a box that stops where the player starts, so the sheet
	     can sit on the page without ever covering the transport. -->
	<div class="relative flex min-h-0 flex-1 flex-col micro:hidden">
		<main
			class:queue-open={dock !== null}
			class="relative m-2 mt-0 flex h-full min-h-0 flex-col rounded-lg bg-dark-0 transition-[margin]"
		>
			{@render children()}
		</main>
		{#if dock}
			<!-- On a phone the sheet is a modal: the strip of page left above it is a way
			     out, not a place to keep working in. Wider, it is a dock beside the page
			     and the page stays live. -->
			<div
				class="absolute inset-0 z-50 bg-dark-0/60 transition-opacity starting:opacity-0 sm:hidden"
				aria-hidden="true"
				onclick={() => (dock = null)}
			></div>
			<!-- a bottom sheet on narrow screens, a dock that narrows the page from lg
			     up. One surface, two tabs — chat and queue never compete for the right
			     edge. -->
			<!-- above the full player (z-40), which is fixed over the whole app: the sheet
			     is reachable from inside that shape, not buried by it -->
			<!-- The grip and the tabs are the handle: pull them down and the sheet goes.
			     The body is left to scroll the queue. -->
			<aside
				{@attach swipe({ down: () => (dock = null), ignore: '[data-sheet-body]' })}
				class="absolute inset-x-0 bottom-0 z-50 flex h-[70dvh] max-h-full flex-col overflow-hidden rounded-panel rounded-b-none border border-haze bg-surface-100/95 backdrop-blur-xl max-sm:translate-y-[max(0px,var(--swipe-y,0px))] max-sm:transition-[translate] max-sm:duration-300 max-sm:ease-[cubic-bezier(0.2,0.7,0.3,1)] max-sm:starting:translate-y-full max-sm:data-swiping:transition-none motion-reduce:transition-none sm:inset-x-auto sm:bottom-20 sm:right-2 sm:top-2 sm:h-auto sm:w-[380px] sm:rounded-b-panel"
			>
				<div class="touch-none sm:hidden" aria-hidden="true">
					<span class="mx-auto mt-2 block h-1 w-9 rounded-full bg-surface-300"></span>
				</div>
				<div class="flex shrink-0 touch-none border-b border-haze sm:touch-auto">
					{#each [{ id: 'queue' as const, label: `Queue · ${queue.items.length}` }, { id: 'chat' as const, label: 'Chat' }] as tab (tab.id)}
						<button
							type="button"
							class="min-h-11 flex-1 px-3 py-2 text-xs font-semibold text-fog hover:text-chalk focus-visible:outline-2 focus-visible:outline-primary-200"
							class:bg-surface-200={dock === tab.id}
							class:text-chalk={dock === tab.id}
							onclick={() => (dock = tab.id)}
						>
							{tab.label}
						</button>
					{/each}
				</div>
				<div data-sheet-body class="flex min-h-0 flex-1 flex-col px-3 pb-3 text-chalk">
					{#if dock === 'queue'}
						<Queue />
					{:else}
						<Chat />
					{/if}
				</div>
			</aside>
		{/if}
	</div>
	<Player bind:dock />
</div>
