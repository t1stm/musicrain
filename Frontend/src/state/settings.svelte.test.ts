import { flushSync } from 'svelte';
import { beforeEach, expect, it, vi } from 'vitest';

// One set of mocks for every fresh copy of the modules below, so the test holds the same
// functions the store under test calls.
const requests = vi.hoisted(() => ({ getSettings: vi.fn(), patchSettings: vi.fn() }));
vi.mock('$requests/accounts', () => requests);

// jsdom hands out no localStorage under vitest's default document origin, and the point
// of these tests is what the store does with one
const kept = new Map<string, string>();
vi.stubGlobal('localStorage', {
	getItem: (key: string) => kept.get(key) ?? null,
	setItem: (key: string, value: string) => kept.set(key, value),
	removeItem: (key: string) => kept.delete(key),
});

// toggling lyrics on asks for the current track's words; nothing is playing, so nothing is asked
vi.stubGlobal('fetch', vi.fn());

beforeEach(() => {
	kept.clear();
	vi.useFakeTimers();
	requests.patchSettings.mockResolvedValue({ settings: {}, updatedUtc: null });
});

/** Every store is a singleton, so each test gets its own copies. */
async function fresh(signedIn: boolean) {
	vi.resetModules();
	// one at a time: in parallel, two imports can each catch svelte's runtime half-loaded
	const settings = (await import('./settings.svelte')).default;
	const account = (await import('./account.svelte')).default;
	const quality = (await import('./quality.svelte')).default;
	const user = (await import('./user.svelte')).default;
	const lyrics = (await import('./lyrics.svelte')).default;

	if (signedIn) {
		account.token = 'tok';
		account.username = 'kris';
	}

	return { settings, account, quality, user, lyrics };
}

/** Runs the effects, then lets every request and the push delay play out. */
async function settle() {
	flushSync();
	await vi.advanceTimersByTimeAsync(1000);
	flushSync();
}

const remote = (settings: Record<string, unknown> | null) =>
	requests.getSettings.mockResolvedValue({ settings, updatedUtc: null });

const sent = () => requests.patchSettings.mock.calls.map(([, patch]) => patch);

it("replaces what was changed while signed out with the account's, on sign-in", async () => {
	const { settings, account, quality } = await fresh(false);
	settings.load();
	quality.codec = 'FLAC';
	await settle();

	remote({ quality: { codec: 'MP3', bitrate: 128 } });
	account.token = 'tok';
	account.username = 'kris';
	await settle();

	expect(quality.codec).toBe('MP3');
	expect(quality.bitrate).toBe(128);
	expect(sent().some((patch) => 'quality' in patch)).toBe(false);
});

it('fills an account that never saved any from this device', async () => {
	kept.set('musicrain.settings', JSON.stringify({ values: { quality: { codec: 'FLAC', bitrate: 320 }, lyricsOpen: true } }));
	remote(null);
	const { settings } = await fresh(true);

	settings.load();
	await settle();

	expect(sent()).toEqual([
		{ quality: { codec: 'FLAC', bitrate: 320 }, chatName: 'kris', lyricsOpen: true },
	]);
});

it("sends a change made offline rather than taking the account's older value", async () => {
	kept.set(
		'musicrain.settings',
		JSON.stringify({ values: { quality: { codec: 'FLAC', bitrate: 320 } }, dirty: ['quality'] }),
	);
	remote({ quality: { codec: 'MP3', bitrate: 128 }, chatName: 'kris', lyricsOpen: false });
	const { settings, quality } = await fresh(true);

	settings.load();
	await settle();

	expect(quality.codec).toBe('FLAC');
	expect(sent()).toEqual([{ quality: { codec: 'FLAC', bitrate: 320 } }]);
});

it('never sends a device-only setting, and never overwrites it', async () => {
	remote({ quality: { codec: 'MP3', bitrate: 128 }, chatName: 'kris', lyricsOpen: false });
	const { settings, quality } = await fresh(true);
	settings.load();
	await settle();

	settings.setDeviceOnly('quality', true);
	quality.codec = 'FLAC';
	await settle();

	expect(sent()).toEqual([]);
	expect(JSON.parse(kept.get('musicrain.settings')!).values.quality.codec).toBe('FLAC');
});

it('leaves a value it cannot read alone, on both sides', async () => {
	remote({ quality: { codec: 'Opus 2', bitrate: 192 }, chatName: 'kris', lyricsOpen: false });
	const { settings, quality } = await fresh(true);

	settings.load();
	await settle();

	expect(quality.codec).toBe('Opus');
	expect(sent()).toEqual([]);
});

it('sends a change a second after it, once', async () => {
	remote({ quality: { codec: 'Opus', bitrate: 192 }, chatName: 'kris', lyricsOpen: false });
	const { settings, quality } = await fresh(true);
	settings.load();
	await settle();

	quality.bitrate = 256;
	flushSync();
	quality.bitrate = 320;
	await settle();

	expect(sent()).toEqual([{ quality: { codec: 'Opus', bitrate: 320 } }]);
});

it('never saves the name Discord gave', async () => {
	remote({ quality: { codec: 'Opus', bitrate: 192 }, chatName: 'kris', lyricsOpen: false });
	const { settings, user } = await fresh(true);
	settings.load();
	await settle();

	user.adopt('discord_name', null);
	await settle();

	expect(sent()).toEqual([]);
	expect(JSON.parse(kept.get('musicrain.settings')!).values.chatName).toBe('kris');
});

it('keeps a change for the next try when Dom cannot be reached, and signs out on a 401', async () => {
	remote({ quality: { codec: 'Opus', bitrate: 192 }, chatName: 'kris', lyricsOpen: false });
	const { settings, account, quality } = await fresh(true);
	settings.load();
	await settle();

	requests.patchSettings.mockRejectedValue(new TypeError('Failed to fetch'));
	quality.codec = 'FLAC';
	await settle();
	expect(JSON.parse(kept.get('musicrain.settings')!).dirty).toEqual(['quality']);

	// the class the fresh modules check against, not the one from before the reset
	const { AudioApiError } = await import('$requests/songs');
	requests.patchSettings.mockRejectedValue(new AudioApiError('Sign in first.', 401));
	quality.codec = 'MP3';
	await settle();
	expect(account.signedIn).toBe(false);
});

it('carries over what the old storage keys remembered', async () => {
	kept.set('musicrain.username', 'Радост');
	kept.set('musicrain.lyrics-open', 'true');
	const { settings, user, lyrics } = await fresh(false);

	settings.load();

	expect(user.username).toBe('Радост');
	expect(lyrics.open).toBe(true);
});
