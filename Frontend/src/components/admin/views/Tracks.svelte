<script lang="ts">
	import Action from '../Action.svelte';
	import CacheBrowser from '../CacheBrowser.svelte';
	import { bytes, clock } from '$lib/admin';
	import fleet from '$states/fleet.svelte';

	type Track = {
		id: string;
		name: string | null;
		artist: string | null;
		album: string | null;
		duration: string | null;
		format: string | null;
		bytes: number | null;
		at: number | null;
	};

	/**
	 * Every track the YouTube or Deezer pod knows, downloaded or only listed by a search. A download
	 * carries its format and size and the buttons that act on the file: promoting is how a track worth
	 * keeping becomes a FLAC, demoting is how the space comes back, and a webm can only be evicted. A
	 * track with no file can only be forgotten — both pods refuse to forget a downloaded one.
	 */
	let { name }: { name: string } = $props();

	const forgetText = (what: string) =>
		`Forget "${what}" from the search cache?\n\nThe next search that lists it adds it back.`;
</script>

<CacheBrowser
	target={name}
	table="tracks"
	columns={['Track', 'Artist', 'Album', 'Length', 'Downloaded', 'ID', '']}
	filter={{ name: 'downloaded', label: 'Downloaded only' }}
>
	{#snippet row(track: Track)}
		{@const what = track.name ?? track.id}
		{@const library = fleet.libraryTarget}
		<td class="wrap">{track.name ?? '—'}</td>
		<td class="wrap">{track.artist ?? '—'}</td>
		<td class="wrap">{track.album ?? '—'}</td>
		<td class="mono">{clock(track.duration)}</td>
		{#if track.format}
			<td title={track.at ? `Downloaded ${new Date(track.at * 1000).toLocaleString()}` : undefined}>
				<span class="rounded-full border border-haze px-2 text-xs text-fog">{track.format}</span>
				<span class="font-mono text-xs">{bytes(track.bytes)}</span>
			</td>
		{:else}
			<td>—</td>
		{/if}
		<td class="mono">{track.id}</td>
		<td>
			{#if track.format}
				{#if track.format === 'flac'}
					<Action
						target={name}
						action="demote"
						params={{ id: track.id }}
						label="Demote to 320"
						confirm={`Replace the FLAC of "${what}" with MP3 320?`}
					/>
				{:else if track.format === 'mp3'}
					<Action target={name} action="promote" params={{ id: track.id }} label="Promote to FLAC" />
				{/if}
				<!-- the one button here that acts on another service, and only for Deezer IDs -->
				{#if library && track.id.startsWith('deezer://')}
					<Action
						target={library}
						action="import-deezer"
						params={{ id: track.id }}
						label="To library"
						confirm={`Copy "${what}" into the library's Deezer folder?\n\nIt becomes an ordinary audio:// song, for you to tidy up in ${library}'s library.`}
					/>
				{/if}
				<Action
					target={name}
					action="evict"
					params={{ id: track.id }}
					label="Evict"
					confirm={`Delete the downloaded file of "${what}"?\n\nIt is downloaded again the next time it is played.`}
				/>
			{:else}
				<Action target={name} action="forget" params={{ id: track.id }} label="Forget" danger confirm={forgetText(what)} />
			{/if}
		</td>
	{/snippet}
</CacheBrowser>
