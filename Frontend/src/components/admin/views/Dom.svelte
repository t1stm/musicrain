<script lang="ts">
	import Action from '../Action.svelte';
	import Cards from '../Cards.svelte';
	import { ago } from '$lib/admin';
	import type { Snapshot } from '$requests/admin';

	type User = {
		username: string;
		role: string | null;
		createdUtc: string;
		activeTokens: number;
		friends: number;
		playlists: number;
	};
	type Playlist = {
		id: string;
		name: string;
		owner: string;
		visibility: string;
		collaborators: number;
		tracks: number;
		duration: string;
		updatedUtc: string;
	};
	type Dom = { adminKeyOutstanding?: boolean; users: User[]; playlists: Playlist[] };

	let { name, snapshot }: { name: string; snapshot: Snapshot } = $props();
	let s = $derived(snapshot as unknown as Dom);

	// Dom refuses these for anybody with a role, whoever asks: the one way an admin with `accounts`
	// could otherwise take over the owner. Disabled here to say so before Dom does.
	const staffNote = 'Staff accounts are managed on the Staff page: remove them from the staff first';
</script>

<Cards
	items={[
		{ value: s.users.length, label: 'accounts' },
		{ value: s.playlists.length, label: 'playlists' }
	]}
/>

{#if s.adminKeyOutstanding}
	<p class="mb-4 text-[0.8125rem] text-gold">An administrator key is waiting to be claimed.</p>
{/if}

<h3 class="eyebrow mb-2">Accounts</h3>
<div class="admin-scroll">
	<table class="admin-table">
		<thead>
			<tr><th>Username</th><th>Role</th><th>Created</th><th>Sessions</th><th>Friends</th><th>Playlists</th><th></th></tr>
		</thead>
		<tbody>
			{#each s.users as user (user.username)}
				{@const staff = !!user.role}
				<tr>
					<td>{user.username}</td>
					<td class="text-fog">{user.role ?? '—'}</td>
					<td>{new Date(user.createdUtc).toLocaleDateString()}</td>
					<td class="mono">{user.activeTokens}</td>
					<td class="mono">{user.friends}</td>
					<td class="mono">{user.playlists}</td>
					<td>
						<Action
							target={name}
							action="rename-user"
							params={{ username: user.username }}
							label="Rename"
							disabled={staff}
							title={staff ? staffNote : undefined}
							ask={{ name: 'name', kind: 'text', initial: user.username, message: `New username for ${user.username}:` }}
						/>
						<Action
							target={name}
							action="sign-out-user"
							params={{ username: user.username }}
							label="Sign out"
							disabled={staff}
							title={staff ? staffNote : undefined}
							confirm={`Sign ${user.username} out of all ${user.activeTokens} session(s)?`}
						/>
						<Action
							target={name}
							action="reset-password"
							params={{ username: user.username }}
							label="Reset password"
							disabled={staff}
							title={staff ? staffNote : undefined}
							ask={{
								name: 'password',
								kind: 'password',
								minLength: 8,
								message: `New password for ${user.username}. This also signs them out everywhere.`
							}}
						/>
						<Action
							target={name}
							action="delete-user"
							params={{ username: user.username }}
							label="Delete"
							danger
							disabled={staff}
							title={staff ? staffNote : undefined}
							confirm={`Delete the account ${user.username} and all ${user.playlists} of its playlists?\n\nThis cannot be undone.`}
						/>
					</td>
				</tr>
			{/each}
		</tbody>
	</table>
</div>

<h3 class="eyebrow mt-6 mb-2">Playlists</h3>
<div class="admin-scroll">
	{#if !s.playlists.length}
		<p class="admin-empty">Nothing here right now.</p>
	{:else}
		<table class="admin-table">
			<thead>
				<tr><th>Name</th><th>Owner</th><th>Tracks</th><th>Duration</th><th>Visibility</th><th>Editors</th><th>Updated</th><th></th></tr>
			</thead>
			<tbody>
				{#each s.playlists as playlist (playlist.id)}
					<tr>
						<td class="wrap">{playlist.name}</td>
						<td>{playlist.owner}</td>
						<td class="mono">{playlist.tracks}</td>
						<td class="mono">{playlist.duration}</td>
						<td>{playlist.visibility}</td>
						<td class="mono">{playlist.collaborators}</td>
						<td>{ago(playlist.updatedUtc)} ago</td>
						<td>
							<Action
								target={name}
								action="rename-playlist"
								params={{ id: playlist.id }}
								label="Rename"
								ask={{ name: 'name', kind: 'text', initial: playlist.name, message: `New name for "${playlist.name}":` }}
							/>
							<Action
								target={name}
								action="set-playlist-public"
								params={{ id: playlist.id, isPublic: playlist.visibility === 'private' }}
								label={playlist.visibility !== 'private' ? 'Make private' : 'Make public'}
							/>
							<Action
								target={name}
								action="delete-playlist"
								params={{ id: playlist.id }}
								label="Delete"
								danger
								confirm={`Delete the playlist "${playlist.name}" (${playlist.owner})?\n\nThis cannot be undone.`}
							/>
						</td>
					</tr>
				{/each}
			</tbody>
		</table>
	{/if}
</div>
