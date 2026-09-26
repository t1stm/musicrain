<script lang="ts">
	import type { SearchResult } from '$states/search.svelte';
	import { Check, Icon, Play, Plus } from 'svelte-hero-icons';
	const { song }: { song: SearchResult } = $props();
	import queue from '$states/queue.svelte';
	import session from '$states/session.svelte';
	import ArtistLink from '$components/ArtistLink.svelte';
	import TrackMenu from '$components/TrackMenu.svelte';
	import { hold } from '$lib/press';
	import { sourceOf } from '$lib/source';

	let source = $derived(sourceOf(song.id));

	// The queue is a dock away and the badge that counts it is at the foot of the
	// screen — on a phone the thumb needs to hear it here, where it tapped.
	let added = $state(false);
	let settle: ReturnType<typeof setTimeout>;

	const onClick = () => {
		queue.add(song);
		added = true;
		clearTimeout(settle);
		settle = setTimeout(() => (added = false), 1100);
	};

	// The artwork is the play button. Same verb as the roll's: it takes over what is
	// playing, and in a room `playNow` sends `add`, so the label has to say which.
	let playLabel = $derived(
		session.inRoom ? `Queue ${song.name} for the room` : `Play ${song.name} now`
	);

	// Where the drop landed, so the ripple spreads from the finger rather than from
	// the middle of a card nobody pointed at. A keyboard press has no point — centre it.
	let splash = $state<{ x: string; y: string; key: number } | null>(null);
	let calm: ReturnType<typeof setTimeout>;

	function playNow(event: MouseEvent) {
		const art = (event.currentTarget as HTMLElement).getBoundingClientRect();
		const pointed = event.detail > 0;
		splash = {
			x: pointed ? `${event.clientX - art.left}px` : '50%',
			y: pointed ? `${event.clientY - art.top}px` : '50%',
			key: Date.now()
		};
		clearTimeout(calm);
		calm = setTimeout(() => (splash = null), 800);

		queue.playNow(song);
	}

	// A held press on the sleeve opens the same menu a row's swipe does. The sleeve keeps
	// the press to itself while it is held: no callout, no selection, no dragged image.
	let menuOpen = $state(false);
</script>

<div data-preview class="group relative flex w-36 shrink-0 flex-col gap-2 sm:w-48">
	<button
		type="button"
		class="art relative block size-36 select-none overflow-hidden rounded-art [-webkit-touch-callout:none] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-200 sm:size-48"
		class:landed={splash !== null}
		aria-label={playLabel}
		onclick={playNow}
		{@attach hold(() => (menuOpen = true))}
	>
		<img
			src={song.thumbnailUrl ?? '/empty.png'}
			alt=""
			draggable="false"
			class="size-full object-cover"
			onerror={(e: Event) => {
				const img = e.currentTarget as HTMLImageElement;
				if (img.src.endsWith('/empty.png')) return;
				img.src = '/empty.png';
			}}
		/>
		<!-- The veil is what makes a white sleeve hold a white glyph. It is the
		     pointer's answer to the persistent corner mark a touch screen gets. -->
		<span class="veil absolute inset-0 bg-dark-0/45" aria-hidden="true"></span>
		<span class="cue absolute grid place-items-center rounded-full bg-primary-600 text-white" aria-hidden="true">
			<Icon src={Play} mini size="18" />
		</span>
		{#if splash}
			{#key splash.key}
				<span class="splash" style:--x={splash.x} style:--y={splash.y} aria-hidden="true"></span>
			{/key}
		{/if}
	</button>
	<!-- Solid fill, not an outline: it is the only thing that stays legible over
	     arbitrary artwork, so the label can stay small and still read. -->
	<span
		class="pointer-events-none absolute left-1.5 top-1.5 rounded-art px-1 font-mono text-[0.55rem] font-bold leading-[1.5] tracking-tight {source.badge}"
		>{source.name}</span
	>

	<div class="flex min-w-0 flex-col">
		<span class="truncate text-sm font-medium text-chalk">{song.name}</span>
		<span class="truncate text-sm text-fog"><ArtistLink artist={song.artist} /></span>
	</div>

	<button
		onclick={onClick}
		aria-label={added ? 'Added to queue' : `Add ${song.name} to queue`}
		class="absolute flex justify-center items-center size-8
	rounded-full cursor-pointer
	right-0 bottom-0 duration-150
	outline-0 opacity-100 pointer-fine:opacity-0 focus-visible:opacity-100 group-hover:opacity-100
	{added ? 'added bg-primary-0' : 'bg-primary-600'}"
	>
		<Icon src={added ? Check : Plus} mini size="20" color="white" />
	</button>

	<TrackMenu result={song} bind:open={menuOpen} trigger={false} />
</div>

<style>
	/* The app's one motion idea is water. A track does not pop into the queue, it
	   lands in it — the ring is the ripple the drop leaves behind. */
	@keyframes ripple {
		from {
			box-shadow: 0 0 0 0 color-mix(in srgb, var(--color-primary-200) 70%, transparent);
		}
		to {
			box-shadow: 0 0 0 12px transparent;
		}
	}

	.added {
		animation: ripple 700ms ease-out;
	}

	/* Playing is the bigger verb, so it gets the bigger version of the same idea:
	   the drop lands on the artwork itself and the ring runs off its edges. */
	@keyframes spread {
		from {
			transform: translate(-50%, -50%) scale(0);
			opacity: 0.85;
		}
		to {
			transform: translate(-50%, -50%) scale(1);
			opacity: 0;
		}
	}

	@keyframes dip {
		0%,
		100% {
			transform: scale(1);
		}
		38% {
			transform: scale(0.955);
		}
	}

	.splash {
		position: absolute;
		left: var(--x);
		top: var(--y);
		width: 260%;
		aspect-ratio: 1;
		border-radius: 999px;
		border: 2px solid var(--color-primary-200);
		pointer-events: none;
		animation: spread 640ms cubic-bezier(0.2, 0.7, 0.3, 1) forwards;
	}

	.art.landed img {
		animation: dip 640ms cubic-bezier(0.2, 0.7, 0.3, 1);
	}

	/* The cue rides the corner on a touch screen — there is no hover to reveal it,
	   and an artwork that plays has to say so before it is pressed. It grows into
	   the middle of the sleeve where a pointer can ask for it. */
	.veil {
		opacity: 0;
		transition: opacity 200ms ease-out;
	}
	.cue {
		left: 0.375rem;
		bottom: 0.375rem;
		width: 1.75rem;
		height: 1.75rem;
		transition:
			left 260ms cubic-bezier(0.2, 0.7, 0.3, 1),
			bottom 260ms cubic-bezier(0.2, 0.7, 0.3, 1),
			width 260ms cubic-bezier(0.2, 0.7, 0.3, 1),
			height 260ms cubic-bezier(0.2, 0.7, 0.3, 1),
			opacity 200ms ease-out;
	}

	@media (hover: hover) and (pointer: fine) {
		.cue {
			opacity: 0;
			left: calc(50% - 1.4rem);
			bottom: calc(50% - 1.4rem);
			width: 2.8rem;
			height: 2.8rem;
		}
		.art:hover .veil,
		.art:focus-visible .veil,
		.art:hover .cue,
		.art:focus-visible .cue {
			opacity: 1;
		}
	}

	@media (prefers-reduced-motion: reduce) {
		.added,
		.splash,
		.art.landed img {
			animation: none;
		}
		.cue,
		.veil {
			transition: none;
		}
	}
</style>
