<script lang="ts">
	import { onMount } from 'svelte';
	import { page } from '$app/state';
	import { resolve } from '$app/paths';
	import ArtistLink from '$components/ArtistLink.svelte';
	import audio from '$states/audio.svelte';
	import current from '$states/current.svelte';
	import queue from '$states/queue.svelte';
	import rooms from '$states/rooms.svelte';
	import session from '$states/session.svelte';
	import user from '$states/user.svelte';

	let roomId = $derived(page.url.searchParams.get('id') ?? '');
	let draftName = $state('');
	let heading = $derived(session.name || 'this room');
	let upNext = $derived(queue.items.slice(queue.currentIndex + 1));

	// Reloading the tab lands here with no interaction behind it, so the browser
	// holds the output silent. Joining anyway would make this client a member the
	// room waits on at every barrier while it plays nothing — so the gate comes
	// first, and the press that clears it is the gesture the browser wanted.
	let held = $derived(audio.blocked === true && !!roomId && user.username !== null);
	let gate = $state<HTMLDialogElement>();
	let releasing = $state(false);
	// the hanging rain, one drop per column, on an irregular line
	const drops = Array.from({ length: 26 }, (_, index) => index);

	$effect(() => {
		if (held && gate && !gate.open) gate.showModal();
	});

	function release() {
		releasing = true;
		// straight out of the click, which is the only place resuming a graph counts
		audio.unblock();
		// the drops need their fall; the connection is already under way behind them
		setTimeout(() => gate?.close(), 340);
	}

	onMount(() => {
		// only for this page's own heading — the layout holds the feed for as long
		// as the session lives. Leaving the route never leaves the room: that is
		// what Leave on the session strip is for.
		rooms.connect();
		return () => rooms.disconnect();
	});

	$effect(() => {
		// `!== false` and not `!audio.blocked`: `null` is the player still building
		// the graph, and joining on that guess is the race the gate exists to lose.
		if (!roomId || user.username === null || audio.blocked !== false) return;
		// going clears `roomId`, which would read as not yet joined and join again,
		// straight into the same close: a tight loop against the server
		if (session.gone === roomId) return;
		if (session.roomId === roomId && session.joinedAs === user.username) return;
		session.connect(roomId, user.username);
	});

	function imageFallback(event: Event) {
		const image = event.currentTarget as HTMLImageElement;
		if (!image.src.endsWith('/empty.png')) image.src = '/empty.png';
	}
</script>

<svelte:head><title>{heading} · musicrain</title></svelte:head>

<div class="page gap-5 p-4 sm:gap-6 sm:p-8 sm:pb-32">
	{#if !roomId}
		<p class="text-sm text-fog">
			No room in the link. <a class="text-primary-500 underline-offset-4 hover:underline" href={resolve('/rooms')}>Browse rooms</a>
		</p>
	{:else if session.gone === roomId}
		<section class="max-w-md">
			<h1 class="font-display mb-2 text-xl font-extralight">That room is gone.</h1>
			<p class="text-sm text-fog">Rooms disappear when the server restarts.</p>
			<a
				href={resolve('/rooms')}
				class="mt-4 inline-flex min-h-11 items-center rounded-row bg-primary-600 px-3 py-2 text-sm font-semibold text-white hover:bg-primary-0"
			>
				Browse rooms
			</a>
		</section>
	{:else if user.username === null}
		<!-- the name is a precondition of joining, so it belongs on the room -->
		<section class="max-w-md">
			<h1 class="font-display mb-2 text-xl font-extralight">Pick a chat name before you join</h1>
			<p class="text-sm text-fog">
				Everyone in the room sees it on your messages. You can change it later.
			</p>
			<form
				class="mt-4 flex flex-col gap-3"
				onsubmit={(event) => {
					event.preventDefault();
					user.choose(draftName);
				}}
			>
				<input
					type="text"
					name="nickname"
					bind:value={draftName}
					maxlength="60"
					autocomplete="nickname"
					aria-label="Your chat name"
					class="rounded-row border border-haze bg-dark-0 text-chalk placeholder:text-fog ring-primary-0 focus:border-primary-0 focus-visible:ring-2"
				/>
				<div class="flex items-center gap-4">
					<button
						type="submit"
						disabled={!draftName.trim()}
						class="min-h-11 rounded-row bg-primary-600 px-3 py-2 text-sm font-semibold text-white hover:bg-primary-0 disabled:opacity-60"
					>
						Join {heading}
					</button>
					<button
						type="button"
						class="text-sm text-fog underline-offset-4 hover:text-chalk hover:underline"
						onclick={() => user.choose('')}
					>
						Join without a name
					</button>
				</div>
			</form>
		</section>
	{:else}
		<section>
			<h2 class="eyebrow mb-3">Now playing</h2>
			{#if current.name}
				<div class="flex items-center gap-4">
					<img
						src={current.thumbnail || '/empty.png'}
						alt=""
						class="size-16 rounded-art object-cover sm:size-20"
						onerror={imageFallback}
					/>
					<div class="min-w-0">
						<p class="font-display truncate text-lg font-normal">{current.name}</p>
						<p class="truncate text-sm text-fog"><ArtistLink artist={current.artist} /></p>
					</div>
				</div>
			{:else}
				<p class="text-sm text-fog">
					Nothing playing. Add something from
					<a class="text-primary-500 underline-offset-4 hover:underline" href={resolve('/search')}>search</a>
					— everyone in the room hears it.
				</p>
			{/if}
		</section>

		<section>
			<h2 class="eyebrow mb-2">Up next · {upNext.length}</h2>
			{#if upNext.length === 0}
				<p class="text-sm text-fog">Nothing queued after this track.</p>
			{:else}
				<ul class="divide-y divide-haze">
					{#each upNext as item, offset (item.id + offset)}
						<li class="flex items-center gap-3 py-2">
							<span class="w-5 text-right font-mono text-[0.68rem] text-fog">{offset + 1}</span>
							<img
								src={item.thumbnailUrl ?? '/empty.png'}
								alt=""
								class="size-9 rounded-art object-cover"
								onerror={imageFallback}
							/>
							<div class="min-w-0 flex-1">
								<p class="truncate text-sm">{item.name}</p>
								<p class="truncate text-xs text-fog"><ArtistLink artist={item.artist} /></p>
							</div>
							<button
								type="button"
								class="flex min-h-11 min-w-11 items-center justify-center rounded-row border border-haze px-2 text-xs hover:bg-surface-200"
								onclick={() => queue.playIndex(queue.currentIndex + offset + 1)}
							>
								Play
							</button>
						</li>
					{/each}
				</ul>
			{/if}
		</section>
	{/if}
</div>

<!-- The gate. The strip already teaches that gold and unfallen droplets mean the
     room is holding for somebody; this is the same hold at full size, and the
     press is what lets the rain down. `showModal` is doing the work a hand-rolled
     overlay would: top layer above the player, focus trapped, backdrop for free. -->
<dialog
	bind:this={gate}
	class="gate"
	data-state={releasing ? 'released' : 'held'}
	aria-labelledby="gate-title"
	oncancel={(event) => event.preventDefault()}
>
	<div class="gate-rain" aria-hidden="true">
		{#each drops as index (index)}
			<span
				class="gate-drop"
				style:--i={index}
				style:--hang={18 + ((index * 11) % 26)}
				style:left="{(index / (drops.length - 1)) * 100}%"
			></span>
		{/each}
	</div>
	<div class="p-5 sm:p-6">
		<p class="eyebrow mb-3 text-gold">held</p>
		<h2 id="gate-title" class="font-display mb-3 text-xl font-extralight text-chalk">
			Nothing plays until you press
		</h2>
		<p class="mb-6 text-sm leading-relaxed text-fog">
			Browsers keep a page silent until someone interacts with it, and this tab loaded straight
			into the room. Press to open the output and drop in wherever the room has got to.
		</p>
		<button
			type="button"
			onclick={release}
			class="min-h-11 w-full rounded-row bg-primary-600 px-4 py-2 text-sm font-semibold text-white hover:bg-primary-0 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-200 sm:w-auto"
		>
			Play the room
		</button>
	</div>
</dialog>
