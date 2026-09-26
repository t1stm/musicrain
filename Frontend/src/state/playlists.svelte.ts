import { audioApi, isDiscordActivity } from '$lib/discord';
import {
	appendTrack,
	createPlaylist,
	deletePlaylist,
	getMyPlaylists,
	getPublicPlaylists,
	patchPlaylist,
	uploadCover,
	type Playlist,
	type PlaylistEdit,
	type PlaylistSummary,
} from '$requests/playlists';
import account from './account.svelte';
import type { SearchResult } from './search.svelte';

/**
 * One cover rule, three outcomes, one place: an uploaded cover, else the first
 * track's artwork, else the app's "no artwork" image. The grid, the hero and the
 * queue footer all read this, so the rule cannot drift between them.
 */
export function coverFor(
	playlist: Pick<PlaylistSummary, 'coverUrl' | 'firstTrackId' | 'firstTrackThumbnailUrl'> & {
		updatedUtc?: string;
	},
) {
	// Dom sends a path under /Audio, not an absolute URL, because only the client knows
	// whether the API is reachable directly or through the activity's /.proxy prefix
	if (playlist.coverUrl) {
		// a cover is served with a week of cache, so a replaced one needs the change
		// stamped into the URL or nobody sees it
		const version = playlist.updatedUtc ? `?v=${Date.parse(playlist.updatedUtc)}` : '';

		return audioApi.replace(/\/Audio$/, '') + playlist.coverUrl + version;
	}
	// inside the activity every artwork host is blocked but the API's own cover route is not
	if (isDiscordActivity && playlist.firstTrackId)
		return `${audioApi}/Cover?id=${encodeURIComponent(playlist.firstTrackId)}`;

	return playlist.firstTrackThumbnailUrl ?? '/empty.png';
}

/**
 * The queue holds `SearchResult`s and a playlist holds snapshots, which is the same
 * fields minus the ones only the player uses. Dropping `contentUrl` is the point:
 * a saved playlist must not carry a URL that expires.
 */
export function toSnapshot(item: SearchResult): SearchResult {
	return {
		id: item.id,
		name: item.name,
		artist: item.artist,
		album: item.album,
		duration: item.duration,
		// inside the activity the thumbnail was rewritten to a proxy path that means
		// nothing outside it — the id is what survives, and `coverFor` re-derives it
		thumbnailUrl: item.thumbnailUrl?.startsWith('/') ? null : item.thumbnailUrl,
	};
}

class Playlists {
	/** Yours, newest change first. Empty until `loadMine` runs. */
	mine: PlaylistSummary[] = $state([]);
	/** Everybody's public ones. Named `shared` because `public` is a keyword in a class body. */
	shared: PlaylistSummary[] = $state([]);
	loading = $state(false);

	error = $state('');

	/**
	 * The public list minus your own. Yours are already on the page under `Yours`,
	 * where the rail says which of them are public — listing them again below is the
	 * same playlist twice.
	 */
	get others() {
		return this.shared.filter(playlist => playlist.owner !== account.username);
	}

	async loadMine() {
		if (!account.token) return (this.mine = []);

		await this.attempt(async token => {
			this.mine = await getMyPlaylists(token);
		});
	}

	async loadPublic() {
		this.shared = await getPublicPlaylists();
	}

	/** Creates one and puts it at the front of `mine`, where Dom would have put it. */
	async save(edit: PlaylistEdit): Promise<Playlist | null> {
		let made: Playlist | null = null;
		await this.attempt(async token => {
			made = await createPlaylist(token, edit);
			this.mine = [made, ...this.mine];
			this.reflect(made);
		});

		return made;
	}

	async update(id: string, edit: PlaylistEdit): Promise<Playlist | null> {
		let changed: Playlist | null = null;
		await this.attempt(async token => {
			changed = await patchPlaylist(token, id, edit);
			this.mine = this.mine.map(p => (p.id === id ? changed! : p));
			this.reflect(changed);
		});

		return changed;
	}

	/**
	 * Puts a track on the end of one of yours: true if it went in, false if it was already
	 * there, null if the add failed. The card changes where it stands rather than jumping to
	 * the front — the list is under a finger in the track menu when this lands.
	 */
	async add(id: string, track: SearchResult): Promise<boolean | null> {
		let added: boolean | null = null;
		await this.attempt(async token => {
			const answer = await appendTrack(token, id, toSnapshot(track));
			this.mine = this.mine.map(p => (p.id === id ? answer.playlist : p));
			this.reflect(answer.playlist);
			added = answer.added;
		});

		return added;
	}

	/** Uploads a cover, then re-reads your list so every card picks up the new URL. */
	async setCover(id: string, file: File): Promise<string | null> {
		let coverUrl: string | null = null;
		await this.attempt(async token => {
			coverUrl = (await uploadCover(token, id, file)).coverUrl;
			await this.loadMine();
		});

		return coverUrl;
	}

	async remove(id: string) {
		await this.attempt(async token => {
			await deletePlaylist(token, id);
			this.mine = this.mine.filter(p => p.id !== id);
			this.shared = this.shared.filter(p => p.id !== id);
		});
	}

	/** Keeps the public list honest about a playlist that just changed visibility. */
	private reflect(playlist: PlaylistSummary) {
		const without = this.shared.filter(p => p.id !== playlist.id);
		this.shared = playlist.isPublic ? [playlist, ...without] : without;
	}

	private async attempt(work: (token: string) => Promise<void>) {
		if (!account.token) return;

		this.loading = true;
		this.error = '';
		try {
			await work(account.token);
		} catch (error) {
			this.error = error instanceof Error ? error.message : 'Could not reach the audio service.';
		} finally {
			this.loading = false;
		}
	}
}

export default new Playlists();
