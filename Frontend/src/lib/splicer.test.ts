import { describe, expect, it, vi } from 'vitest';
import { Splicer, TAIL, type Track } from './splicer';

class FakeSource {
	buffer: AudioBuffer | null = null;
	onended: (() => void) | null = null;
	/** the context time this node was told to start at, or null while unstarted */
	at: number | null = null;
	offset = 0;
	stopped = false;
	/** the context time it was told to stop at, or null for "right now" */
	stoppedAt: number | null = null;

	connect() {}
	disconnect() {}

	start(at = 0, offset = 0) {
		this.at = at;
		this.offset = offset;
	}

	stop(at: number | null = null) {
		this.stopped = true;
		this.stoppedAt = at;
	}
}

class FakeContext {
	currentTime = 0;
	sources: FakeSource[] = [];

	createBufferSource() {
		const source = new FakeSource();
		this.sources.push(source);
		return source;
	}
}

function bench() {
	const context = new FakeContext();
	const splicer = new Splicer(context as unknown as AudioContext, {} as AudioNode);
	const track = (key: string, duration: number, partial = false): Track => ({
		key,
		buffer: { duration } as AudioBuffer,
		partial
	});
	const buffer = (duration: number) => ({ duration }) as AudioBuffer;

	return { context, splicer, track, buffer };
}

describe('Splicer', () => {
	it('hands the next track the exact context time the current buffer runs out', () => {
		const { context, splicer, track } = bench();

		splicer.start(track('one', 180));
		context.currentTime = 30;
		splicer.queue(track('two', 200));

		expect(context.sources[1].at).toBe(180);
		expect(splicer.queued).toBe('two');
	});

	it('starts the next track from its own beginning, not from the join', () => {
		const { context, splicer, track } = bench();

		splicer.start(track('one', 180));
		splicer.queue(track('two', 200));

		expect(context.sources[1].offset).toBe(0);
	});

	it('schedules against the origin a seek moved, not the position it left', () => {
		const { context, splicer, track } = bench();

		splicer.start(track('one', 180));
		context.currentTime = 10;
		splicer.seek(60);
		splicer.queue(track('two', 200));

		// 120 s of the track are left, so the join is 120 s from now
		expect(context.sources[2].at).toBe(130);
	});

	it('drops a start time it cannot honour once the tail is already playing', () => {
		const { context, splicer, track } = bench();

		splicer.start(track('one', 180));
		context.currentTime = 181;
		splicer.queue(track('two', 200));

		expect(splicer.queued).toBe('');
		expect(context.sources).toHaveLength(1);
	});

	it('carries on from the queued node when the current one ends', () => {
		const { context, splicer, track } = bench();
		const advanced = vi.fn();
		splicer.onadvance = advanced;

		splicer.start(track('one', 180));
		splicer.queue(track('two', 200));
		context.currentTime = 180;
		context.sources[0].onended?.();

		expect(advanced).toHaveBeenCalledOnce();
		expect(splicer.key).toBe('two');
		expect(splicer.queued).toBe('');
		// the position is the new track's, from the clock the join was made on
		context.currentTime = 185;
		expect(splicer.position()).toBe(5);
	});

	it('reports the end only when nothing was queued behind the track', () => {
		const { context, splicer, track } = bench();
		const ended = vi.fn();
		splicer.onend = ended;

		splicer.start(track('one', 180));
		context.currentTime = 180;
		context.sources[0].onended?.();

		expect(ended).toHaveBeenCalledOnce();
		expect(splicer.running).toBe(false);
	});

	it('does not read a node it stopped itself as the track ending', () => {
		const { context, splicer, track } = bench();
		const ended = vi.fn();
		splicer.onend = ended;

		splicer.start(track('one', 180));
		context.currentTime = 10;
		splicer.seek(60);
		context.sources[0].onended?.();

		expect(ended).not.toHaveBeenCalled();
	});

	it('holds the position across a pause and drops the join with it', () => {
		const { context, splicer, track } = bench();

		splicer.start(track('one', 180));
		splicer.queue(track('two', 200));
		context.currentTime = 45;
		splicer.pause();

		expect(splicer.position()).toBe(45);
		expect(splicer.queued).toBe('');
		expect(context.sources[1].stopped).toBe(true);

		context.currentTime = 300;
		splicer.resume();
		// resumed where it was held, not where the clock ran on to
		expect(context.sources[2].offset).toBe(45);
		expect(splicer.position()).toBe(45);
	});

	it('never runs past the end of the buffer it was given', () => {
		const { splicer, track } = bench();

		splicer.start(track('one', 180), 400);

		expect(splicer.position()).toBe(180);
	});

	it('stops a partial buffer short of the samples the cut spoiled', () => {
		const { context, splicer, track } = bench();

		splicer.start(track('one', 30, true));

		expect(context.sources[0].stoppedAt).toBe(30 - TAIL);
		expect(splicer.decoded).toBe(30 - TAIL);
	});

	it('splices a longer decode in at a sample the running one has not reached', () => {
		const { context, splicer, track, buffer } = bench();

		splicer.start(track('one', 30, true));
		context.currentTime = 10;
		splicer.upgrade(buffer(60), true);

		const [first, second] = context.sources;
		// the old node plays on until the swap, the new one picks up that same sample
		expect(first.stoppedAt).toBe(10 + TAIL);
		expect(second.at).toBe(10 + TAIL);
		expect(second.offset).toBe(10 + TAIL);
		// and the clock the position is read from does not move
		expect(splicer.position()).toBe(10);
	});

	it('reads a partial buffer running out as a stall, not as the track ending', () => {
		const { context, splicer, track, buffer } = bench();
		const ended = vi.fn();
		splicer.onend = ended;

		splicer.start(track('one', 30, true));
		context.currentTime = 30 - TAIL;
		context.sources[0].onended?.();

		expect(ended).not.toHaveBeenCalled();
		expect(splicer.running).toBe(false);
		expect(splicer.position()).toBe(30 - TAIL);

		// and the decode that lands after it carries on from where the bytes stopped
		context.currentTime = 31;
		splicer.upgrade(buffer(60), true);

		expect(splicer.running).toBe(true);
		expect(context.sources[1].offset).toBe(30 - TAIL);
	});

	it('leaves a stalled track alone while it is paused', () => {
		const { context, splicer, track, buffer } = bench();

		splicer.start(track('one', 30, true));
		context.currentTime = 10;
		splicer.pause();
		splicer.upgrade(buffer(60), true);

		expect(splicer.running).toBe(false);
		expect(splicer.position()).toBe(10);
	});

	it('reports the end once the whole track is decoded and it runs out', () => {
		const { context, splicer, track, buffer } = bench();
		const ended = vi.fn();
		splicer.onend = ended;

		splicer.start(track('one', 30, true));
		context.currentTime = 10;
		splicer.upgrade(buffer(180), false);
		context.currentTime = 180;
		context.sources[1].onended?.();

		expect(ended).toHaveBeenCalledOnce();
	});

	it('refuses a join while either side of it is a partial buffer', () => {
		const { splicer, track, buffer } = bench();

		splicer.start(track('one', 30, true));
		splicer.queue(track('two', 200));
		expect(splicer.queued).toBe('');

		splicer.upgrade(buffer(180), false);
		splicer.queue(track('two', 200, true));
		expect(splicer.queued).toBe('');

		splicer.queue(track('two', 200));
		expect(splicer.queued).toBe('two');
	});

	it('runs the longer buffer from where it is when the swap is too late to schedule', () => {
		const { context, splicer, track, buffer } = bench();

		splicer.start(track('one', 30, true));
		// inside the last `TAIL` of what is decoded: there is no room to schedule
		context.currentTime = 30 - TAIL / 2;
		splicer.upgrade(buffer(60), true);

		const [first, second] = context.sources;
		expect(first.stoppedAt).toBe(null);
		expect(second.at).toBe(30 - TAIL / 2);
		expect(second.offset).toBe(30 - TAIL);
	});
});
