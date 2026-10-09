import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { PlayBody } from '$requests/history';
import type { SearchResult } from '$states/search.svelte';

// The two requests the tracker makes, mocked; everything else in the module is the real thing.
const requests = vi.hoisted(() => ({ putPlay: vi.fn(), claimHistory: vi.fn() }));
vi.mock('$requests/history', async (original) => ({
	...(await original<typeof import('$requests/history')>()),
	...requests,
}));

// jsdom hands out no localStorage under vitest's default document origin
const kept = new Map<string, string>();
vi.stubGlobal('localStorage', {
	getItem: (key: string) => kept.get(key) ?? null,
	setItem: (key: string, value: string) => kept.set(key, value),
	removeItem: (key: string) => kept.delete(key),
});
vi.stubGlobal('fetch', vi.fn());

const start = new Date('2026-10-09T12:00:00Z');

beforeEach(() => {
	kept.clear();
	vi.useFakeTimers({ toFake: ['Date'] });
	vi.setSystemTime(start);
	requests.putPlay.mockResolvedValue(204);
});

/** Every store is a singleton, so each test gets its own copies. */
async function fresh() {
	vi.resetModules();
	const history = (await import('./history.svelte')).default;
	const queue = (await import('./queue.svelte')).default;
	const current = (await import('./current.svelte')).default;
	const account = (await import('./account.svelte')).default;
	return { history, queue, current, account };
}

/** Lets every flush run out: they are promise chains, done before the next macrotask. */
const settle = () => new Promise((done) => setTimeout(done, 0));

const track = (id: string): SearchResult => ({
	id,
	name: id,
	artist: 'artist',
	duration: '00:03:00',
	thumbnailUrl: null,
});

/** Each play as it was last sent, in the order the plays began — which is the order they are sent in. */
function plays() {
	const last = new Map<string, PlayBody>();
	for (const [id, body] of requests.putPlay.mock.calls as [string, PlayBody][]) last.set(id, { ...body });
	return [...last.values()];
}

const outbox = () => JSON.parse(kept.get('musicrain.history-outbox') ?? '{}') as Record<string, unknown>;

describe('time heard', () => {
	it('counts playing, and neither a seek nor a pause', async () => {
		const { history, queue } = await fresh();
		queue.playNow(track('a'));

		for (const seconds of [0.5, 1, 1, 40, 40.5, 35, 35.25]) history.tick(seconds);
		history.end('finished');
		await settle();

		// 0.5 + 0.5, nothing paused, the jump to 40 skipped, 0.5, the jump back skipped, 0.25
		expect(plays()[0].playedMs).toBe(1750);
	});

	it('writes once when half a minute has been heard, and not again until the end', async () => {
		const { history, queue } = await fresh();
		queue.playNow(track('a'));
		await settle();

		for (let seconds = 0.25; seconds <= 45; seconds += 0.25) history.tick(seconds);
		await settle();
		const [, counted, ...rest] = requests.putPlay.mock.calls as [string, PlayBody][];

		expect(counted[1].playedMs).toBeGreaterThanOrEqual(30_000);
		expect(counted[1].endReason).toBeNull();
		expect(rest).toEqual([]);
	});
});

describe('reasons', () => {
	it('every way a track changes ends the last play and starts the next the way §B2 says', async () => {
		const { history, queue, current } = await fresh();

		queue.add(track('a')); // into an empty queue
		queue.playNow(track('b'));
		queue.replaceWith([track('c'), track('d'), track('e')]);
		queue.nextTrack('next');
		queue.previousTrack();
		queue.previousTrack(); // at the top: the same track again
		queue.removeIndex(0); // the current one
		history.end('finished'); // what the player does when a track runs out
		queue.nextTrack('autoplay');
		history.end('error');
		queue.nextTrack('autoplay'); // past the end: nothing new begins
		history.begin(track('room'), 'room');
		history.begin(track('room 2'), 'room');
		current.clear();
		await settle();

		expect(plays().map((play) => [play.trackId, play.startReason, play.endReason])).toEqual([
			['a', 'chosen', 'replaced'],
			['b', 'chosen', 'replaced'],
			['c', 'collection', 'skipped'],
			['d', 'next', 'previous'],
			['c', 'previous', 'previous'],
			['c', 'previous', 'replaced'],
			['d', 'autoplay', 'finished'],
			['e', 'autoplay', 'error'],
			['room', 'room', 'skipped'],
			['room 2', 'room', 'stopped'],
		]);
		expect(plays().at(-1)!.sourceKind).toBe('room');
	});

	it('ends the last track of the queue finished, with nothing after it', async () => {
		const { history, queue } = await fresh();
		queue.playNow(track('a'));
		history.end('finished');
		queue.nextTrack('autoplay');
		await settle();

		expect(plays()).toHaveLength(1);
		expect(plays()[0].endReason).toBe('finished');
	});

	it('records where a track came from, and whether the order was shuffled', async () => {
		const { queue } = await fresh();
		queue.replaceWith([{ ...track('a'), origin: { kind: 'album', id: 'artist — record' } }, track('b'), track('c'), track('d')]);
		queue.shuffle();
		queue.nextTrack('next');
		queue.replaceWith([track('e')]);
		await settle();

		const [first, second, third] = plays();
		expect([first.sourceKind, first.sourceId, first.shuffled]).toEqual(['album', 'artist — record', false]);
		expect([second.sourceKind, second.sourceId, second.shuffled]).toEqual(['queue', null, true]);
		expect(third.shuffled).toBe(false);
	});

	it('records nothing while paused', async () => {
		const { history, queue } = await fresh();
		history.paused = true;
		queue.playNow(track('a'));
		await settle();

		expect(requests.putPlay).not.toHaveBeenCalled();
	});
});

describe('sessions', () => {
	it('stay one session across plays less than half an hour apart', async () => {
		const { history, queue } = await fresh();
		queue.playNow(track('a'));
		vi.setSystemTime(start.getTime() + 29 * 60_000);
		queue.playNow(track('b'));
		history.end('finished');
		vi.setSystemTime(start.getTime() + 61 * 60_000);
		queue.playNow(track('c'));
		await settle();

		const [a, b, c] = plays();
		expect(b.sessionId).toBe(a.sessionId);
		expect([a.sessionPosition, b.sessionPosition]).toEqual([0, 1]);
		expect(c.sessionId).not.toBe(a.sessionId);
		expect(c.sessionPosition).toBe(0);
	});
});

describe('outbox', () => {
	it('keeps what did not land and sends it again with the next play', async () => {
		const { queue } = await fresh();
		requests.putPlay.mockResolvedValue(0);
		queue.playNow(track('a'));
		await settle();
		expect(Object.keys(outbox())).toHaveLength(1);

		requests.putPlay.mockResolvedValue(204);
		queue.playNow(track('b'));
		await settle();

		expect(outbox()).toEqual({});
		expect(new Set(requests.putPlay.mock.calls.map(([id]) => id)).size).toBe(2);
	});

	it('drops what the server refused for good, and keeps what it could not take yet', async () => {
		const { history, queue } = await fresh();
		requests.putPlay.mockResolvedValue(400);
		queue.playNow(track('refused'));
		history.end('finished');
		await settle();
		expect(outbox()).toEqual({});

		requests.putPlay.mockResolvedValue(503);
		queue.playNow(track('later'));
		await settle();
		expect(Object.values(outbox()).map((entry) => (entry as { body: PlayBody }).body.trackId)).toEqual(['later']);
	});

	it("never sends one account's plays under another, and sends anonymous ones under whoever is in", async () => {
		const { history, account } = await fresh();
		const body = (trackId: string) => ({ trackId, startedUtc: start.toISOString() }) as PlayBody;
		kept.set(
			'musicrain.history-outbox',
			JSON.stringify({
				theirs: { body: body('ana'), account: 'ana' },
				nobodys: { body: body('anonymous'), account: null },
			}),
		);
		account.token = 'tok';
		account.username = 'Kris';

		await history.flush();

		expect(requests.putPlay.mock.calls.map(([id, , token]) => [id, token])).toEqual([['nobodys', 'tok']]);
		expect(outbox()).toEqual({});
	});

	it('holds the newest 200 plays of the last 30 days', async () => {
		const { history } = await fresh();
		requests.putPlay.mockResolvedValue(0);
		const entries = Object.fromEntries(
			Array.from({ length: 205 }, (_, index) => [
				`play ${index}`,
				{ body: { trackId: 'a', startedUtc: new Date(start.getTime() - index * 60_000).toISOString() }, account: null },
			]),
		);
		entries.ancient = { body: { trackId: 'a', startedUtc: new Date(start.getTime() - 31 * 86_400_000).toISOString() }, account: null };
		kept.set('musicrain.history-outbox', JSON.stringify(entries));

		await history.flush();

		const left = Object.keys(outbox());
		expect(left).toHaveLength(200);
		expect(left).not.toContain('ancient');
		expect(left).not.toContain('play 204');
		expect(left).toContain('play 0');
	});
});
