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
	import settings from '$states/settings.svelte';
	import user from '$states/user.svelte';

	let { children } = $props();
	let dock = $state<'queue' | 'chat' | null>(null);

	// The app's one back handler: everything opened on top of the page closes before back
	// leaves it. The sheet is a layer like any other — on a phone it is most of the screen.
	watchBackNavigation();
	closeOnBack(() => dock !== null, () => (dock = null));

	// a swipe turns the sheet's tabs the way the finger goes, onto the one beyond that edge
	const turn = { left: () => (dock = 'queue'), right: () => (dock = 'chat') };

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
		account.load();
		// after the account, so the first thing it does is read the account's settings
		settings.load();
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
	<Header onaccount={() => (dock = null)} />
	{#if session.inRoom}
		<SessionStrip />
	{/if}
	<!-- Page and dock share a box that stops where the player starts, so the sheet
	     can sit on the page without ever covering the transport. On a phone the box
	     clips too: a sheet pulled down goes behind the player's edge, not over it. -->
	<div class="relative flex min-h-0 flex-1 flex-col max-sm:overflow-clip micro:hidden">
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
				class="absolute inset-0 z-50 bg-dark-0/60 transition-opacity starting:opacity-0 has-[+aside[data-swiped=down]]:opacity-0 sm:hidden"
				aria-hidden="true"
				onclick={() => (dock = null)}
			></div>
			<!-- a bottom sheet on narrow screens, a dock that narrows the page from lg
			     up. One surface, two tabs — chat and queue never compete for the right
			     edge. On a phone it sits in the page's own frame: `main`'s margins, its
			     8px corners, a gap above the player — and a close carries it that gap
			     further, past the clipped edge. -->
			<!-- above the full player (z-40), which is fixed over the whole app: the sheet
			     is reachable from inside that shape, not buried by it -->
			<!-- The grip and the tabs are the handle: pull them down and the sheet goes —
			     on down off the screen, the shade lifting with it, before it is gone.
			     Sideways they turn the strip, chat to the left and queue to the right — and
			     so does the body, below.
			     The body is left to scroll the queue, except where it marks a part that does
			     not scroll as `data-sheet-handle` — the queue's now-playing block. -->
			<aside
				{@attach swipe({
					...turn,
					down: () => (dock = null),
					ignore: '[data-sheet-body]',
					handle: '[data-sheet-handle]'
				})}
				class="absolute inset-x-2 bottom-2 z-50 flex max-sm:[view-transition-name:sheet] h-[70dvh] max-h-[calc(100%-0.5rem)] flex-col overflow-hidden rounded-panel border border-haze bg-surface-100/95 backdrop-blur-xl max-sm:translate-y-[max(0px,var(--swipe-y,0px))] max-sm:transition-[translate] max-sm:duration-300 max-sm:ease-[cubic-bezier(0.2,0.7,0.3,1)] max-sm:data-swiping:transition-none max-sm:data-[swiped=down]:translate-y-[calc(100%+0.5rem)] motion-reduce:transition-none sm:inset-x-auto sm:bottom-20 sm:right-2 sm:top-2 sm:h-auto sm:w-[380px]"
			>
				<div class="touch-none py-2 sm:hidden" aria-hidden="true">
					<span class="mx-auto block h-1 w-9 rounded-full bg-surface-300"></span>
				</div>
				<div class="flex shrink-0 touch-none border-b border-haze sm:touch-auto">
					{#each [{ id: 'chat' as const, label: 'Chat' }, { id: 'queue' as const, label: `Queue · ${queue.items.length}` }] as tab (tab.id)}
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
				<!-- The tabs are a strip, chat on the left and queue on the right, the order the
				     player's own buttons keep. Both stay mounted side by side and the strip
				     slides between them, so a finger can pull the next one in: anywhere in the
				     body, or on the tabs. Up and down stay the scroll's (`touch-pan-y`), so only
				     a sideways drag reaches `swipe`. The queue's rows keep a sideways swipe for
				     their own actions, a field keeps its caret, and the now-playing block is
				     already the sheet's handle. -->
				<div
					data-sheet-body
					{@attach swipe({ ...turn, ignore: '.swipe-row, [data-sheet-handle], input, textarea' })}
					class="flex min-h-0 flex-1 touch-pan-y flex-col overflow-clip text-chalk"
				>
					<div class="strip" style:--tab={dock === 'queue' ? 1 : 0}>
						<div inert={dock !== 'chat'}><Chat /></div>
						<div inert={dock !== 'queue'}><Queue /></div>
					</div>
				</div>
			</aside>
		{/if}
	</div>
	<Player bind:dock />
</div>

<style>
	/* One pane wide, two panes long. `--tab` says which one shows and the finger's
	   offset pulls the other in from its edge, never past either end. Under the finger
	   nothing eases; let go and it settles on whichever tab is now open. */
	.strip {
		display: grid;
		flex: 1;
		min-height: 0;
		grid-template-columns: 100% 100%;
		grid-template-rows: minmax(0, 1fr);
		translate: clamp(-100%, calc(var(--tab) * -100% + var(--swipe-x, 0px)), 0%) 0;
		transition: translate 280ms cubic-bezier(0.2, 0.7, 0.3, 1);
	}
	.strip > div {
		display: flex;
		min-height: 0;
		flex-direction: column;
		padding: 0 0.75rem 0.75rem;
	}
	:global([data-swiping]) .strip {
		transition: none;
	}
	@media (prefers-reduced-motion: reduce) {
		.strip {
			transition: none;
		}
	}
</style>
