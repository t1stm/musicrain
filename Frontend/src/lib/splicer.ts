/** A decoded track, keyed by whatever identity the caller compares tracks on.
 *  `partial` marks a buffer that is only the part of the track decoded so far. */
export type Track = { key: string; buffer: AudioBuffer; partial?: boolean };

/**
 * How much of a partial buffer is not worth playing.
 *
 * A prefix of a stream decodes to the PCM the whole file decodes to, sample for
 * sample, except right at the cut: measured against every format the API serves,
 * the last 16 samples differ, where the decoder ran out of input mid-filter. Fifty
 * milliseconds is that with room to spare, and it is also the notice the audio
 * thread needs to splice the next buffer in before this one runs out.
 */
export const TAIL = 0.05;

type Scheduled = Track & { node: AudioBufferSourceNode; origin: number };

/**
 * Two decoded tracks on one audio clock, spliced by `start(when)`.
 *
 * The point of the whole class is the one line in `queue()`: the next track is
 * handed the exact context time the current buffer runs out, so the join is made
 * in the audio thread and nothing on the main thread can be late for it. A timer
 * cannot do this — a background tab clamps `setTimeout` to about a second, which
 * is an audible hole between two tracks that are supposed to be continuous.
 *
 * Position is the clock minus an origin, so a seek is nothing but a new origin.
 */
export class Splicer {
	#context: AudioContext;
	#output: AudioNode;

	/** The node making sound, the track it holds, and the context time that
	 *  track's position 0 sits at. */
	#node: AudioBufferSourceNode | null = null;
	#track: Track | null = null;
	#origin = 0;
	/** Where playback resumes from while no node is running. */
	#held = 0;
	/** The next track, already holding a start time on the audio clock. */
	#ahead: Scheduled | null = null;
	/** Whether the sound is meant to be running — so an upgrade landing on a track
	 *  that stalled can start it again, and one landing on a paused track cannot. */
	#playing = false;

	/** The sound has moved on to the track handed to `queue()`. Fired from the
	 *  node's own `ended`, so it arrives just after a join that already happened. */
	onadvance: () => void = () => {};
	/** The last track ran out with nothing queued behind it. */
	onend: () => void = () => {};

	constructor(context: AudioContext, output: AudioNode) {
		this.#context = context;
		this.#output = output;
	}

	/** The key of the track being played, or '' before one is handed over. */
	get key() {
		return this.#track?.key ?? '';
	}

	/** The key of the track holding a start time, or '' if nothing is queued. */
	get queued() {
		return this.#ahead?.key ?? '';
	}

	get running() {
		return this.#node !== null;
	}

	/** How far into the current track there is sound to play: the whole thing, or
	 *  as much of a partial buffer as is worth playing. */
	get decoded() {
		return this.#track ? this.#usable(this.#track) : 0;
	}

	position() {
		if (!this.#node) return this.#held;
		return Math.max(this.#context.currentTime - this.#origin, 0);
	}

	/** The track that plays now, dropping whatever was playing or queued. */
	start(track: Track, from = 0, play = true) {
		this.#clear();
		this.#track = track;
		this.#held = this.#within(from, track);
		this.#playing = play;
		if (play) this.#run(this.#held);
	}

	/**
	 * The same track, with more of it decoded. The extra samples are the same
	 * samples the whole file decodes to, so the swap needs no crossfade and no
	 * alignment: the new node is handed the sample the running one has not reached
	 * yet, and the origin it is playing against does not move.
	 */
	upgrade(buffer: AudioBuffer, partial = false) {
		const track = this.#track;
		if (!track) return;

		const running = this.#node;
		// where the buffer playing right now runs out, before the longer one replaces it
		const edge = this.#origin + this.#usable(track);
		this.#track = { key: track.key, buffer, partial };

		// stalled on the end of the last prefix, or paused: the longer buffer is
		// picked up from where the shorter one stopped.
		if (!running) {
			if (this.#playing) this.#run(this.#held);
			return;
		}

		const at = this.#context.currentTime + TAIL;
		if (at >= edge) {
			// too close to the edge to schedule anything: the node is ending underneath
			// us, so run from where the sound actually stopped — which is the end of the
			// old buffer once the clock is past it, not where the clock has got to.
			const reached = Math.min(this.#context.currentTime, edge) - this.#origin;
			this.#held = this.#within(reached, this.#track);
			this.#end(running);
			this.#node = null;
			if (this.#playing) this.#run(this.#held);
			return;
		}

		this.#retire(running, at);
		this.#run(at - this.#origin, at);
	}

	pause() {
		this.#playing = false;
		if (!this.#node) return;
		this.#held = this.position();
		this.#end(this.#node);
		this.#node = null;
		// the join was scheduled against a clock the sound is no longer riding: it
		// has to be handed a new start time once playback resumes.
		this.unqueue();
	}

	resume() {
		this.#playing = true;
		if (this.#node || !this.#track) return;
		this.#run(this.#held);
	}

	seek(to: number) {
		this.#held = this.#track ? this.#within(to, this.#track) : Math.max(to, 0);
		this.unqueue();
		if (!this.#node) return;
		this.#end(this.#node);
		this.#node = null;
		this.#run(this.#held);
	}

	/**
	 * Hands the next track its start time — the exact end of the current buffer —
	 * so the join is already made before anything on the main thread hears about
	 * it. Ignored while nothing is playing, or when something is queued already.
	 */
	queue(track: Track) {
		if (!this.#node || !this.#track || this.#ahead) return;
		// a partial buffer ends where the bytes stopped, not where the track does, so
		// neither side of a join can be one: the start time would land mid-song.
		if (this.#track.partial || track.partial) return;

		const at = this.#origin + this.#track.buffer.duration;
		// past the join: the current track is already inside its own tail, and a
		// start time in the past would play the next one from the middle.
		if (at <= this.#context.currentTime) return;

		const node = this.#context.createBufferSource();
		node.buffer = track.buffer;
		node.connect(this.#output);
		node.onended = () => this.#finished(node);
		node.start(at);
		this.#ahead = { ...track, node, origin: at };
	}

	unqueue() {
		if (!this.#ahead) return;
		this.#end(this.#ahead.node);
		this.#ahead = null;
	}

	stop() {
		this.#clear();
		this.#held = 0;
		this.#playing = false;
	}

	dispose() {
		this.onadvance = () => {};
		this.onend = () => {};
		this.#clear();
	}

	#run(from: number, at = this.#context.currentTime) {
		const track = this.#track;
		if (!track) return;

		const node = this.#context.createBufferSource();
		node.buffer = track.buffer;
		node.connect(this.#output);
		node.onended = () => this.#finished(node);

		node.start(at, from);
		// a partial buffer is stopped short of the samples the cut spoiled; running
		// out of it is a stall, which `#finished` tells apart by the track's own flag.
		if (track.partial) node.stop(at + Math.max(this.#usable(track) - from, 0));
		this.#node = node;
		this.#origin = at - from;
	}

	#finished(node: AudioBufferSourceNode) {
		// a node we replaced ourselves: its stop() is not the track ending.
		if (node !== this.#node) return;

		// the decoded part ran out, not the track: hold where the bytes stopped and
		// wait for `upgrade` to hand over more of it.
		if (this.#track?.partial) {
			this.#held = this.#usable(this.#track);
			this.#node = null;
			return;
		}

		const ahead = this.#ahead;
		if (!ahead) {
			this.#node = null;
			this.#held = 0;
			this.onend();
			return;
		}

		// The join already happened, in the audio thread. Everything here is
		// bookkeeping catching up to sound that is already playing.
		this.#node = ahead.node;
		this.#track = { key: ahead.key, buffer: ahead.buffer };
		this.#origin = ahead.origin;
		this.#ahead = null;
		this.onadvance();
	}

	#clear() {
		this.unqueue();
		if (this.#node) this.#end(this.#node);
		this.#node = null;
		this.#track = null;
	}

	/** Lets a node play on until `at` without its `ended` reading as the track
	 *  finishing — the half of a splice that steps aside. */
	#retire(node: AudioBufferSourceNode, at: number) {
		node.onended = () => node.disconnect();
		node.stop(at);
	}

	/** Stops a node without its `ended` reading as the track finishing. */
	#end(node: AudioBufferSourceNode) {
		node.onended = null;
		node.stop();
		node.disconnect();
	}

	#within(seconds: number, track: Track) {
		return Math.min(Math.max(seconds, 0), this.#usable(track));
	}

	/** How much of a track's buffer is worth playing. */
	#usable(track: Track) {
		return track.partial ? Math.max(track.buffer.duration - TAIL, 0) : track.buffer.duration;
	}
}
