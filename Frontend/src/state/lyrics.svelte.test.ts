import { describe, expect, it, beforeEach, vi } from 'vitest';
import lyrics from './lyrics.svelte';
import current from './current.svelte';
import { AudioApiError } from '$requests/songs';

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

const body = {
	type: 'Synchronized',
	source: 'LRCLIB',
	lines: [{ at: 0, text: 'Alle warten' }],
	text: 'Alle warten',
	matched: { title: 'Sonne', artist: 'Rammstein', length: 272 }
};

function answering(status: number, payload: unknown = body, delayMs = 0) {
	return vi.fn(async () => {
		if (delayMs) await new Promise((resolve) => setTimeout(resolve, delayMs));
		return { ok: status < 400, status, json: async () => payload } as unknown as Response;
	});
}

// jsdom hands out no localStorage under vitest's default document origin, and one of
// these tests is about what the state does with one.
const kept = new Map<string, string>();
vi.stubGlobal('localStorage', {
	getItem: (key: string) => kept.get(key) ?? null,
	setItem: (key: string, value: string) => kept.set(key, value),
	removeItem: (key: string) => kept.delete(key)
});

// A fresh ID per test: the state remembers which track it has, on purpose, and reusing
// one ID would have the second test answered by the first test's load.
let track = 0;

beforeEach(() => {
	kept.clear();
	// Toggling `open` loads through the global fetch; the tests below hand `load` their
	// own, so this only has to be something that resolves rather than something real.
	vi.stubGlobal('fetch', answering(204, null));

	current.id = `audio://${++track}`;
	lyrics.open = false;
	lyrics.lyrics = null;
	lyrics.status = 'idle';
	lyrics.activeIndex = -1;
});

describe('load', () => {
	it('fetches nothing while the pane is closed', async () => {
		const fetcher = answering(200);
		lyrics.load(fetcher);
		await flush();

		expect(fetcher).not.toHaveBeenCalled();
		expect(lyrics.status).toBe('idle');
	});

	it('is ready once the words arrive', async () => {
		lyrics.open = true;
		lyrics.load(answering(200));
		await flush();

		expect(lyrics.status).toBe('ready');
		expect(lyrics.lyrics?.type).toBe('Synchronized');
	});

	it('sets none for a 204', async () => {
		lyrics.open = true;
		lyrics.load(answering(204, null));
		await flush();

		expect(lyrics.status).toBe('none');
		expect(lyrics.lyrics).toBeNull();
	});

	it('sets error and leaves the words null when the service throws', async () => {
		lyrics.open = true;
		lyrics.load(answering(500, {}));
		await flush();

		expect(lyrics.status).toBe('error');
		expect(lyrics.lyrics).toBeNull();
	});

	// The bug this class exists to make impossible: skipping tracks quickly is exactly
	// how someone ends up watching the wrong song's words.
	it('discards a slow answer for a track that is no longer playing', async () => {
		lyrics.open = true;
		lyrics.load(answering(200, { ...body, text: 'track A' }, 20));

		current.id = 'audio://b';
		lyrics.load(answering(200, { ...body, text: 'track B' }));

		await new Promise((resolve) => setTimeout(resolve, 40));

		expect(lyrics.lyrics?.text).toBe('track B');
	});

	it('does not refetch the same track when the pane is toggled off and on', async () => {
		const fetcher = answering(200);
		lyrics.open = true;
		lyrics.load(fetcher);
		await flush();

		lyrics.open = false;
		lyrics.open = true;
		lyrics.load(fetcher);
		await flush();

		expect(fetcher).toHaveBeenCalledTimes(1);
	});

	it('clears rather than asking when nothing is playing', async () => {
		lyrics.open = true;
		const fetcher = answering(200);

		current.id = '';
		lyrics.load(fetcher);
		await flush();

		expect(fetcher).not.toHaveBeenCalled();
		expect(lyrics.status).toBe('idle');
		expect(lyrics.activeIndex).toBe(-1);
	});

	it('tries again after an error, unlike after an answer', async () => {
		lyrics.open = true;
		lyrics.status = 'idle';
		lyrics.load(answering(500, {}));
		await flush();

		const second = answering(200);
		lyrics.load(second);
		await flush();

		expect(second).toHaveBeenCalledTimes(1);
		expect(lyrics.status).toBe('ready');
	});
});

describe('open', () => {
	it('is remembered on this device', () => {
		lyrics.open = true;
		expect(kept.get('musicrain.lyrics-open')).toBe('true');

		lyrics.open = false;
		expect(kept.get('musicrain.lyrics-open')).toBe('false');
	});

	it('survives storage that throws rather than answering', () => {
		vi.stubGlobal('localStorage', {
			getItem: () => {
				throw new DOMException('denied', 'SecurityError');
			},
			setItem: () => {
				throw new DOMException('denied', 'SecurityError');
			}
		});

		expect(() => (lyrics.open = true)).not.toThrow();
		expect(lyrics.open).toBe(true);
	});
});

describe('AudioApiError', () => {
	it('is what the request module throws, so the state can tell it apart', () => {
		expect(new AudioApiError('boom', 500)).toBeInstanceOf(Error);
	});
});
