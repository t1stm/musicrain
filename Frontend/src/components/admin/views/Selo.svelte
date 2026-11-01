<script lang="ts">
	import Action from '../Action.svelte';
	import Cards from '../Cards.svelte';
	import type { Snapshot } from '$requests/admin';

	type Room = {
		roomId: string;
		name: string;
		users: { id: string; username: string | null }[];
		player: {
			current: { name: string | null; artist: string | null } | null;
			queueLength: number;
			currentIndex: number;
			playing: boolean;
			positionSeconds: number;
		};
	};
	type Selo = { count: number; rooms: Room[] };

	let { name, snapshot }: { name: string; snapshot: Snapshot } = $props();
	let s = $derived(snapshot as unknown as Selo);
</script>

<Cards
	items={[
		{ value: s.count, label: 'rooms' },
		{ value: s.rooms.reduce((n, room) => n + room.users.length, 0), label: 'listeners' }
	]}
/>

<div class="admin-scroll">
	{#if !s.rooms.length}
		<p class="admin-empty">Nothing here right now.</p>
	{:else}
		<table class="admin-table">
			<thead><tr><th>Room</th><th>Listeners</th><th>Playing</th><th>Queue</th><th>Position</th><th></th></tr></thead>
			<tbody>
				{#each s.rooms as room (room.roomId)}
					<tr>
						<td class="wrap">{room.name}</td>
						<td class="wrap">
							{#each room.users as listener (listener.id)}
								<Action
									target={name}
									action="kick"
									params={{ room: room.roomId, user: listener.id }}
									label="{listener.username ?? 'anonymous'} ✕"
									confirm={`Remove ${listener.username ?? 'this anonymous listener'} from "${room.name}"?`}
								/>
							{:else}—{/each}
						</td>
						<td class="wrap">
							{room.player.current ? `${room.player.current.name ?? '?'} — ${room.player.current.artist ?? '?'}` : '—'}
						</td>
						<td class="mono">
							{room.player.queueLength ? `${room.player.currentIndex + 1}/${room.player.queueLength}` : 'empty'}
						</td>
						<td class="mono">{room.player.playing ? `${Math.round(room.player.positionSeconds)}s` : 'paused'}</td>
						<td>
							<Action
								target={name}
								action="close-room"
								params={{ room: room.roomId }}
								label="Close room"
								danger
								confirm={`Close "${room.name}" and disconnect its ${room.users.length} listener(s)?`}
							/>
						</td>
					</tr>
				{/each}
			</tbody>
		</table>
	{/if}
</div>
