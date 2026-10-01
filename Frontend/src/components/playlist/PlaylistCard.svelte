<script lang="ts">
	import { resolve } from '$app/paths';
	import { convertTimeSpanStringToSeconds, getTimeString } from '$lib';
	import PlaylistCover from './PlaylistCover.svelte';
	import type { PlaylistSummary } from '$requests/playlists';

	let { playlist }: { playlist: PlaylistSummary } = $props();

	let length = $derived(getTimeString(convertTimeSpanStringToSeconds(playlist.duration)));
</script>

<a
	href={`${resolve('/playlist')}?id=${playlist.id}`}
	class="group flex flex-col overflow-hidden rounded-panel border border-haze bg-surface-100 outline-none hover:border-surface-300 focus-visible:ring-2 focus-visible:ring-primary-500"
>
	<!-- One pixel carries the state, the way the room rail does: violet is public,
	     pale violet is friends, haze is private. The word itself is said once, in the
	     group heading above. -->
	<span
		class="h-px w-full shrink-0"
		class:bg-primary-0={playlist.visibility === 'public'}
		class:bg-primary-500={playlist.visibility === 'friends'}
		class:bg-haze={playlist.visibility === 'private'}
	></span>
	<div class="p-2">
		<PlaylistCover
			{playlist}
			class="aspect-square w-full rounded-art object-cover"
			alt=""
		/>
		<p class="mt-2 truncate text-sm font-semibold">{playlist.name}</p>
		<p class="truncate font-mono text-[0.68rem] text-fog">
			{playlist.trackCount} · {length}
		</p>
	</div>
</a>
