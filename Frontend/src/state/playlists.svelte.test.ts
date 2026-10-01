import { expect, it } from 'vitest';
import { PUBLIC_API_URL } from '$env/static/public';
import playlists, { coverFor, toSnapshot } from './playlists.svelte';
import account from './account.svelte';
import type { SearchResult } from './search.svelte';

const summary = (over: Partial<Parameters<typeof coverFor>[0]> = {}) => ({
	coverUrl: null,
	firstTrackId: null,
	firstTrackThumbnailUrl: null,
	...over,
});

it('prefers an uploaded cover, then the first track, then the empty image', () => {
	expect(coverFor(summary({ coverUrl: '/Audio/Playlists/p_1/Cover' }))).toBe(
		`${PUBLIC_API_URL}/Playlists/p_1/Cover`,
	);
	expect(coverFor(summary({ firstTrackThumbnailUrl: 'https://img/1.jpg' }))).toBe(
		'https://img/1.jpg',
	);
	expect(coverFor(summary())).toBe('/empty.png');
});

// an uploaded cover wins even when the playlist also has tracks
it('does not fall through to the first track once a cover exists', () => {
	const cover = coverFor(
		summary({
			coverUrl: '/Audio/Playlists/p_1/Cover',
			firstTrackThumbnailUrl: 'https://img/1.jpg',
		}),
	);

	expect(cover).toContain('/Playlists/p_1/Cover');
});

const track: SearchResult = {
	id: 'local://1',
	name: 'Ноќ',
	artist: 'Someone',
	album: 'An album',
	contentUrl: 'https://api.gergov.bg/Audio/DownloadRaw?id=local://1',
	duration: '00:03:41',
	thumbnailUrl: 'https://img/1.jpg',
};

it('saves the fields a row draws and drops the ones that expire', () => {
	expect(toSnapshot(track)).toEqual({
		id: 'local://1',
		name: 'Ноќ',
		artist: 'Someone',
		album: 'An album',
		duration: '00:03:41',
		thumbnailUrl: 'https://img/1.jpg',
	});
});

// inside the Discord activity thumbnails are rewritten to /.proxy paths, which mean
// nothing to a browser outside it
it('drops a proxied thumbnail rather than storing a path only Discord can read', () => {
	expect(
		toSnapshot({
			...track,
			thumbnailUrl: '/.proxy/api/Audio/Cover?id=local://1',
		}).thumbnailUrl,
	).toBe(null);
});

// yours are on the page already, under Yours, where the rail says which are public
it('keeps your own playlists out of the everybody-else list', () => {
	const card = (id: string, owner: string) => ({ id, owner }) as never;
	playlists.shared = [card('p_1', 't1stm'), card('p_2', 'someone'), card('p_3', 't1stm')];

	account.username = 't1stm';
	expect(playlists.others.map(p => p.id)).toEqual(['p_2']);

	// signed out, everything public is somebody else's
	account.username = null;
	expect(playlists.others).toHaveLength(3);
});

// a friend's public playlist you can edit is already on the page, under Shared with you
it('keeps the playlists you edit out of the everybody-else list', () => {
	const card = (id: string, owner: string) => ({ id, owner }) as never;
	playlists.shared = [card('p_1', 'ana'), card('p_2', 'boyan')];
	playlists.mine = [card('p_1', 'ana')];

	account.username = 'kris';
	expect(playlists.others.map(p => p.id)).toEqual(['p_2']);

	playlists.mine = [];
});
