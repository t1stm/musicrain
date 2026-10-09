import type { SearchResult } from '$states/search.svelte';
import { convertTimeSpanStringToSeconds } from '$lib';
import { downloadUrl, takePrefetched } from '$requests/songs';
import history from './history.svelte';

class Current {
	/** The platform ID, kept only so the player can say which service the track came from. */
	id: string = $state('');
	name: string = $state('');
	artist: string = $state('');
	album: string = $state('');
	url: string = $state('');
	lengthSeconds: number = $state(0);
	thumbnail: string = $state('');

	/** The object URL the current track is playing from, when it came from the
	 *  prefetch. Held so it can be revoked — on the *next* change, not this one:
	 *  the retry path in `Audio.svelte` re-requests the current resource, and a
	 *  revoked URL is not there to re-request. */
	#objectUrl = '';

	set(now: SearchResult) {
		this.#release();
		const prefetched = takePrefetched(now.id);
		this.#objectUrl = prefetched ?? '';

		this.id = now.id;
		this.name = now.name;
		this.artist = now.artist;
		this.album = now.album ?? '';
		this.url = prefetched ?? downloadUrl(now.id);
		this.lengthSeconds = convertTimeSpanStringToSeconds(now.duration);
		this.thumbnail = now.thumbnailUrl ?? '/empty.png';
	}

	#release() {
		if (!this.#objectUrl) return;
		URL.revokeObjectURL(this.#objectUrl);
		this.#objectUrl = '';
	}

	clear() {
		history.end('stopped');
		this.#release();
		this.id = '';
		this.name = '';
		this.artist = '';
		this.album = '';
		this.url = '';
		this.lengthSeconds = 0;
		this.thumbnail = '/empty.png';
	}
}

export default new Current();
