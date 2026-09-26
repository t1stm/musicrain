/**
 * Holding a room's clients on the server's clock.
 *
 * The room's shared position lives on the server as a monotonic stopwatch, and
 * `sync` is the only way to read it. The reply is already stale by the time it
 * lands, by however long the return trip took, so a client that compares its
 * position against the raw number is not measuring its offset — it is measuring
 * the difference between its uplink and its downlink, which is close to zero on
 * a symmetric path however far behind the room it actually is. That is why the
 * old half-second tolerance never fired even with clients a third of a second
 * apart: each one looked, honestly, fine to itself.
 *
 * Compensating for that is the whole feature. `reported + rtt/2` is the server's
 * position at the moment the reply arrived, and the difference against the local
 * position at that same moment is the real error.
 */

/** Samples the offset is drawn from. */
export const syncWindow = 16;
/**
 * How long the link measurements — the round trip and the server-clock offset —
 * are kept for.
 *
 * These describe the path, not the track, so they are worth remembering far
 * longer than a position is: the estimate is a *minimum* over the window, and
 * the more of the window is clean the closer that minimum sits to the true
 * one-way delay. A window this long also outlives a route change on its own,
 * which is the only thing that invalidates them.
 *
 * A mean over the same span would be strictly worse and is the trap here — a
 * round trip can only ever be inflated, never shortened, so averaging folds
 * every queueing spike straight into the answer while the minimum ignores them.
 */
export const linkWindowSeconds = 90;
/** Rate correction per second of error. Doubles as the loop's time constant:
 *  the residual is erased over roughly 1/gain seconds. */
export const proportionalGain = 0.15;
/**
 * How far out the room has to be before the loop starts correcting at all.
 *
 * The upper half of a hysteresis pair — see `syncTargetSeconds` for the lower.
 * A single threshold cannot do this job: a loop that stops correcting at the
 * same place it starts settles *on* the threshold, where the jitter the estimate
 * carries walks it back and forth across the line. On a real laptop against a
 * LAN server that was an error parked at 20–28 ms against a 25 ms line, and a
 * rate change every few samples for it, forever.
 */
export const syncDeadbandSeconds = 0.035;
/**
 * Where the loop lets go once it has engaged, and what it aims at while steering.
 *
 * Small enough that landing here is genuinely together, far enough under
 * `syncDeadbandSeconds` that ordinary jitter cannot span the gap: with drift of
 * tens of ppm, walking the 25 ms between the two takes several minutes, so that
 * is roughly how often the rate moves at all once a track has settled.
 *
 * Steering aims past it, at zero, on purpose. A correction that faded out as it
 * approached the target would approach it and never cross — the same never-
 * arriving loop, relocated. Aiming at zero puts the release point a finite
 * ~11 seconds away, at which point the rate goes back to exactly 1.
 */
export const syncTargetSeconds = 0.01;
/**
 * The widest the band may open on a bad link.
 *
 * Kept well under `hardSeekSeconds`: the band is where the loop *stops* correcting,
 * so a band that reached the jump threshold would mean a client could sit at the
 * edge of an audible seek indefinitely and never be steered off it.
 */
export const maxDeadbandSeconds = 0.25;
/**
 * Samples the window needs before the rate is allowed to move at all.
 *
 * `error` is a minimum over the window, and a minimum over one sample is just that
 * sample — the raw jitter of a single trip, with nothing filtered out of it. A
 * track change empties the window, so the readings right after one are exactly the
 * unfiltered ones, and steering on them is steering on noise: measured at a burst
 * of a dozen-odd rate changes in the first seconds of every track. The opening snap
 * has already put this client in roughly the right place, so there is nothing to
 * chase in the meantime.
 */
export const minSteeringSamples = 4;
/** The 98–102 % budget. 2 % is 34 cents of pitch; the loop only spends that
 *  much while more than 133 ms out, and holds within ±0.2 % (3.5 cents, under
 *  the JND) once settled. */
export const maxRateDeviation = 0.02;
/** Past this, steering is hopeless arithmetic — 2 % closes 20 ms a second — so
 *  jump instead and take the audible seam. */
export const hardSeekSeconds = 0.75;
/** The looser threshold that applies to the first reading of a track. A join or
 *  a track change starts everyone from a `seek` frame that was already one
 *  downlink stale, which on a slow link is a third of a second to steer off —
 *  fifteen seconds of it at 2 %. A jump in the first moment of a track is not
 *  audible the way fifteen seconds of being late is. */
export const firstReadingSnapSeconds = 0.05;
/** `sync` replies carry no request id, so exactly one may be in flight; this is
 *  the floor on top of that, not the interval. */
export const minSyncSpacingMs = 250;
/**
 * The spacing once the track has had its opening correction and the room is
 * inside the deadband.
 *
 * Polling four times a second is what convergence costs, not what holding costs.
 * Once every frame that moves the shared clock carries the moment it was sent,
 * the only thing left for `sync` to catch is the device's own clock drift, and
 * that is tens of ppm — a 50 ppm device slips 3 ms a minute, so a reading every
 * two seconds is over a hundred times faster than the error it is watching for.
 * Any real disturbance arrives as a stamped frame, which is placed exactly and
 * without waiting for a poll at all.
 */
export const settledSyncSpacingMs = 2000;
/** Samples the drift regression runs over. Clock drift is tens of ppm — a 50 ppm
 *  device accumulates 3 ms a minute — so it does not rise out of the noise for
 *  several minutes. Read it, do not act on it: see `drift`. */
export const driftWindow = 240;
/** Below either of these the regression is fitting noise: a 50 ppm device
 *  accumulates 3 ms a minute, so a window shorter than this cannot separate it
 *  from the jitter it is buried in. */
export const driftMinimumSamples = 60;
export const driftMinimumSpanSeconds = 300;

export type SyncSample = {
	err: number;
	rtt: number;
	/** Correction already spent when this was taken, so a sample can be aged
	 *  forward instead of expiring — see `error`. */
	spent: number;
};

/** One reading of the link: how long the trip took, and where the server's clock
 *  stood relative to this client's `performance.now()`. */
export type LinkSample = { at: number; rtt: number; skew: number | null };

const clamp = (value: number, low: number, high: number) => Math.min(high, Math.max(low, value));

/** Every action the loop takes, in the browser only — the room simulator and the
 *  tests import this same module and would drown in it. On unless Settings → Advanced
 *  turns it off.
 *  ponytail: a boolean, not log levels. Add levels when one is actually wanted. */
const inBrowser = typeof window !== 'undefined' && !import.meta.env?.VITEST;
let debug = inBrowser;

export const logSync = (on: boolean) => (debug = on && inBrowser);
const ms = (seconds: number) => `${(seconds * 1000).toFixed(1)}ms`;
const log = (action: string, detail: string) => {
	if (debug) console.log(`[syncClock] ${action}: ${detail}`);
};

/** Least-squares slope of `open` against `at`, in seconds per second. */
function slope(history: { at: number; open: number }[]) {
	const n = history.length;
	const meanAt = history.reduce((sum, p) => sum + p.at, 0) / n;
	const meanOpen = history.reduce((sum, p) => sum + p.open, 0) / n;

	let covariance = 0;
	let variance = 0;
	for (const point of history) {
		covariance += (point.at - meanAt) * (point.open - meanOpen);
		variance += (point.at - meanAt) ** 2;
	}
	return variance === 0 ? 0 : covariance / variance;
}

export class SyncClock {
	/** The rate last handed out by `rateFor`, applied by the player. */
	rate = 1;

	private samples: SyncSample[] = [];
	/** The link alone, which survives `reset` because it measures the path, not
	 *  the track — and the lead on a stamped frame is needed most in the moment
	 *  right after a track change, when there are no samples left to derive it
	 *  from. Kept by age rather than by count: see `linkWindowSeconds`. */
	private links: LinkSample[] = [];
	/** Correction already spent, so the device's own error can be recovered from
	 *  a loop whose entire job is hiding it. */
	private corrected = 0;
	private lastSampleAt: number | null = null;
	/** Whether this track has had its opening correction yet. Read from outside to
	 *  decide how hard to poll: a track still converging wants every reading it
	 *  can get, a settled one is only watching for drift. */
	settled = false;
	private history: { at: number; open: number }[] = [];
	/** Whether the loop currently has hold of the rate. Latched: it takes
	 *  `syncDeadbandSeconds` to engage and `syncTargetSeconds` to let go, and the
	 *  gap between the two is what keeps the rate still. */
	steering = false;

	/**
	 * One `sync` round trip.
	 *
	 * @param sentAt       client clock when the request went out, ms
	 * @param receivedAt   client clock when the reply landed, ms
	 * @param reported     the position the server replied with, seconds
	 * @param position     local audible position at `receivedAt`, seconds
	 * @param serverUtcMs  when the server read that position, Unix ms, when the
	 *                     reply carried a stamp
	 * @returns how far behind the room this client is, seconds. Positive means
	 *          the room is ahead and the player has to catch up.
	 */
	sample(sentAt: number, receivedAt: number, reported: number, position: number, serverUtcMs?: number) {
		if (!Number.isFinite(reported)) {
			log('sample dropped', `server reported ${reported}`);
			return this.error;
		}

		const rtt = Math.max(0, receivedAt - sentAt) / 1000;
		const at = receivedAt / 1000;

		// Ahead of the push, so this sample records the correction standing at the
		// moment it was taken rather than one interval short of it.
		if (this.lastSampleAt !== null) this.corrected += (this.rate - 1) * (at - this.lastSampleAt);
		this.lastSampleAt = at;

		// the reply describes the server one return trip ago; carry it to now
		const err = reported + rtt / 2 - position;
		this.samples.push({ err, rtt, spent: this.corrected });
		if (this.samples.length > syncWindow) this.samples.shift();

		// NTP's offset from three timestamps: the server stood at `serverUtcMs`
		// when this client's clock read the midpoint of the trip. Held against
		// `performance.now()` rather than `Date.now()` on purpose — the wall clock
		// steps when the OS corrects it, and a step in the middle of a track would
		// be indistinguishable from the room having moved.
		const skew =
			serverUtcMs === undefined || !Number.isFinite(serverUtcMs) ? null : serverUtcMs - (sentAt + receivedAt) / 2;
		this.links.push({ at, rtt, skew });
		this.links = this.links.filter((link) => at - link.at <= linkWindowSeconds);

		// the sample's own offset plus the correction spent to date is the offset
		// this device would have had with no steering at all, so its slope is the
		// device's clock rate error
		this.history.push({ at, open: err + this.corrected });
		if (this.history.length > driftWindow) this.history.shift();

		log(
			'sample',
			`raw=${ms(err)} filtered=${ms(this.error)} rtt=${ms(rtt)} ` +
				`skew=${skew === null ? 'unstamped' : `${skew.toFixed(0)}ms`} ` +
				`spent=${ms(this.corrected)} samples=${this.samples.length}`
		);
		return this.error;
	}

	/**
	 * The least-queued sample of the window, which is NTP's trick for NTP's
	 * reason: a round trip can only ever be inflated, never shortened, so the
	 * fastest one seen is the one whose two halves are closest to equal, and the
	 * symmetry assumption is least wrong there. Averaging instead would fold
	 * every queueing spike straight into the estimate.
	 */
	get error() {
		if (this.samples.length === 0) return 0;
		const best = this.samples.reduce((previous, s) => (s.rtt < previous.rtt ? s : previous));
		// The cleanest sample in the window may be seconds old, and the loop has
		// been steering since — which is exactly the correction that has already
		// closed part of what it measured. Netting it out is what lets the window
		// outlive the polling interval instead of expiring with it.
		return best.err - (this.corrected - best.spent);
	}

	/** One-way latency estimate, seconds — what an unstamped `seek` frame is
	 *  stale by, and the fallback for a stamped one before any reply has landed. */
	get halfRtt() {
		if (this.links.length === 0) return 0;
		return Math.min(...this.links.map((link) => link.rtt)) / 2;
	}

	/**
	 * Milliseconds to add to `performance.now()` to read the server's UTC clock,
	 * from the least-queued trip of the window — same reason as `error`: a round
	 * trip can only be inflated, so the quickest one seen is where the halves are
	 * closest to equal and the symmetry assumption is least wrong.
	 *
	 * `null` until a stamped reply has landed, which is what keeps this working
	 * against a server that does not stamp.
	 */
	get skew(): number | null {
		const stamped = this.links.filter((link) => link.skew !== null);
		if (stamped.length === 0) return null;
		return stamped.reduce((previous, link) => (link.rtt < previous.rtt ? link : previous)).skew;
	}

	/**
	 * How long a frame stamped `serverUtcMs` spent in flight, in seconds.
	 *
	 * `halfRtt` is a *minimum* over the window, so it describes the best trip the
	 * link has managed lately — and a broadcast frame is under no obligation to be
	 * that trip. A `seek` that spent 300 ms in a queue was being placed 60 ms back,
	 * and the client landed a quarter of a second ahead of the room at exactly the
	 * moment everybody was listening for it. This measures the frame that actually
	 * arrived. It also puts the server's own handling time on the uplink where it
	 * belongs, rather than splitting it across both halves the way `rtt / 2` does.
	 *
	 * What it does **not** fix is a path whose asymmetry is constant: a link that is
	 * always 180 ms up and 20 ms down biases `skew` by half the difference, and the
	 * stamp then reads back exactly `halfRtt` again. That is NTP's floor, not an
	 * implementation gap — three timestamps cannot separate a clock offset from a
	 * lopsided path.
	 *
	 * Falls back to `halfRtt` for an unstamped frame, or before the offset is known.
	 */
	stalenessOf(serverUtcMs: number, at: number) {
		const skew = this.skew;
		if (skew === null || !Number.isFinite(serverUtcMs)) {
			log('staleness', `unstamped frame, assuming halfRtt=${ms(this.halfRtt)}`);
			return this.halfRtt;
		}
		// a frame cannot land before it was sent, so a negative reading is the
		// offset estimate being wrong rather than the frame being early
		const staleness = Math.max(0, (at + skew - serverUtcMs) / 1000);
		log('staleness', `frame spent ${ms(staleness)} in flight, skew=${skew.toFixed(0)}ms`);
		return staleness;
	}

	/**
	 * The device's clock rate error against the server's, as a fraction —
	 * positive when this device's audio clock runs fast, which is the sign the
	 * loop has to cancel: it settles on `1 - drift` to hold station. Diagnostics
	 * only, and `null` until there is enough history to mean anything.
	 *
	 * Nothing feeds this back into the rate, because the loop already absorbs
	 * drift: a proportional controller against a plant running `d` fast settles
	 * at `d / gain` of error, which is 0.8 ms at 120 ppm. An integral term for it
	 * would buy under a millisecond and cost the windup that comes with one.
	 */
	get drift(): number | null {
		const last = this.history.at(-1);
		if (last === undefined || this.history.length < driftMinimumSamples) return null;
		const span = last.at - this.history[0].at;
		if (span < driftMinimumSpanSeconds) return null;
		return -slope(this.history);
	}

	/**
	 * How far out this client may sit before the loop touches the rate — a floor of
	 * `syncDeadbandSeconds`, opened up to whatever noise this particular link
	 * carries.
	 *
	 * A fixed band cannot serve both ends of the range. The error estimate is only
	 * ever as steady as the trips it is drawn from, so on a link whose round trip
	 * wanders by 180 ms the estimate wanders too, and a 25 ms band is crossed
	 * several times a second by noise alone — measured at 27 rate changes a minute
	 * on the 600 ms satellite, which is the artifact the band exists to prevent.
	 * Widening the band globally to quiet that client costs a LAN client, whose
	 * estimate is steady to a millisecond, sixty milliseconds of accuracy it was
	 * never going to lose.
	 *
	 * So each client gets the band its own link asks for: the typical excess of a
	 * round trip over the quickest one seen, which is exactly the dispersion that
	 * ends up in the estimate. A steady link keeps the floor and stays accurate; a
	 * jittery one opens up and stays quiet. Below a few samples there is nothing to
	 * measure dispersion from, so the floor stands.
	 */
	get deadband() {
		if (this.links.length < 4) return syncDeadbandSeconds;
		const trips = this.links.map((link) => link.rtt).sort((a, b) => a - b);
		return clamp(trips[trips.length >> 1] - trips[0], syncDeadbandSeconds, maxDeadbandSeconds);
	}

	/**
	 * Whether this error wants a jump rather than the rate. True for anything the
	 * rate budget cannot close in reasonable time, and for the first reading of a
	 * track, where a jump costs nothing and steering costs a long slow arrival.
	 */
	shouldSeek(error: number) {
		const threshold = this.settled ? hardSeekSeconds : firstReadingSnapSeconds;
		const seek = Math.abs(error) > threshold;
		if (seek)
			log('seek', `err=${ms(error)} past ${this.settled ? 'hard' : 'first reading'} ` + `threshold=${ms(threshold)}`);
		return seek;
	}

	/**
	 * The playback rate that closes `error`, over roughly `1 / gain` seconds.
	 *
	 * Latching, not a dead zone: nothing happens until the error passes the band,
	 * and then the loop steers on the whole error — not the part beyond the band —
	 * until it is inside `syncTargetSeconds`, where it releases to exactly 1 and
	 * stays there. What that buys over a dead zone is stillness: a dead zone puts
	 * the resting point on the threshold, so noise alone keeps nudging the rate,
	 * while this one rests at 1 with 40 ms of room on either side of it.
	 *
	 * The cost is a step at the moment it engages, up to 0.75 %. That is a tenth
	 * of the pitch budget, paid once every several minutes instead of a smaller
	 * one paid every few seconds.
	 */
	rateFor(error: number) {
		const previous = this.rate;
		const unfiltered = this.samples.length < minSteeringSamples;
		this.settled = true;
		// nothing filtered yet, so there is nothing here worth steering on
		if (unfiltered) this.steering = false;
		else if (Math.abs(error) > this.deadband) this.steering = true;
		else if (Math.abs(error) <= syncTargetSeconds) this.steering = false;

		this.rate = this.steering ? clamp(1 + proportionalGain * error, 1 - maxRateDeviation, 1 + maxRateDeviation) : 1;
		if (this.rate !== previous)
			log(
				'rate',
				`${previous.toFixed(4)} -> ${this.rate.toFixed(4)} err=${ms(error)} ` +
					`${this.steering ? 'steering to' : 'released at'} ${ms(syncTargetSeconds)}, ` +
					`engages past ${ms(this.deadband)}` +
					(unfiltered ? ` (held at 1: ${this.samples.length} samples)` : '')
			);
		return this.rate;
	}

	/** A track change or a join: the samples describe a position that no longer
	 *  exists, and the next reading is allowed its opening snap. The drift history
	 *  survives on purpose — it describes the device, not the track. */
	reset() {
		log('reset', `track change or join, dropping ${this.samples.length} samples`);
		this.samples = [];
		this.rate = 1;
		this.steering = false;
		this.lastSampleAt = null;
		this.settled = false;
	}

	/** After acting on `shouldSeek`. The window has to go with the position it
	 *  measured, or the same stale error is applied again on the next reading and
	 *  the correction runs away from itself; and this track has now had its one
	 *  free jump, so what follows is steering. */
	seeked() {
		log('seeked', `jumped, dropping ${this.samples.length} samples and steering from here`);
		this.samples = [];
		this.rate = 1;
		this.steering = false;
		this.settled = true;
	}
}
