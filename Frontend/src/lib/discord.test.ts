import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PUBLIC_API_URL } from '$env/static/public';

async function loadAt(href: string) {
	vi.resetModules();
	vi.stubGlobal('location', new URL(href));
	return import('./discord');
}

beforeEach(() => {
	vi.unstubAllGlobals();
});

describe('discord activity detection', () => {
	it('leaves URLs alone in a normal browser tab', async () => {
		const { isDiscordActivity, audioApi } = await loadAt('https://music.gergov.bg/');

		expect(isDiscordActivity).toBe(false);
		expect(audioApi).toBe(PUBLIC_API_URL);
	});

	it('detects the activity from the discordsays host and from ?frame_id', async () => {
		expect((await loadAt('https://1234.discordsays.com/')).isDiscordActivity).toBe(true);
		expect((await loadAt('https://music.gergov.bg/?frame_id=abc')).isDiscordActivity).toBe(true);
	});

	it('takes every image from the API origin, the one the activity can reach', async () => {
		const { audioApi } = await loadAt('https://1234.discordsays.com/');
		const { cover } = await import('./cover');

		expect(audioApi).toBe('/.proxy/api/Audio');
		expect(cover('https://i9.ytimg.com/vi/abc/hq.jpg?sqp=1', 160)).toBe(
			'/.proxy/api/Audio/Cover/Get?url=https%3A%2F%2Fi9.ytimg.com%2Fvi%2Fabc%2Fhq.jpg%3Fsqp%3D1&size=160'
		);
		expect(cover('cover://asdf.png')).toBe('/.proxy/api/Audio/Cover/Get?url=cover%3A%2F%2Fasdf.png');
		expect(cover('/Audio/Playlists/p_1/Cover')).toBe('/Audio/Playlists/p_1/Cover');
		expect(cover(null)).toBe(undefined);
	});
});
