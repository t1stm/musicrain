import type { SearchResult } from '$states/search.svelte';
import { audioWsUrl, proxyThumbnails } from '$lib/discord';
import queue from './queue.svelte';
import current from './current.svelte';
import audio from './audio.svelte';
import rooms from './rooms.svelte';
import skipped from './skipped.svelte';
import { isUnnamed, roomLabel } from '$requests/rooms';
import { SyncClock, minSyncSpacingMs, settledSyncSpacingMs } from '$lib/syncClock';

/** What the room's `queue` frame actually carries: the raw platform result, not
 *  the search shape. No `contentUrl`, and most fields are nullable. */
type RoomQueueItem = {
	id: string;
	name: string | null;
	artist: string | null;
	album: string | null;
	duration: string;
	thumbnailUrl: string | null;
	originalTitle: string | null;
	originalArtist: string | null;
};

export type ChatLine = {
	id: number;
	username: string;
	text: string;
	system: boolean;
	at: string;
};

/** The strip speaks in the wire's own words: a shared clock that drifts reads as
 *  a bug when it is unlabelled and as a state when it is named. */
export type SessionStatus =
	| 'offline'
	| 'connecting'
	| 'holding'
	| 'synced'
	| 'catching up'
	| 'paused'
	| 'stopped'
	| 'reconnecting';

const maximumChatLines = 200;
// A `sync` reply that never comes must not wedge the loop shut, and only one may
// be in flight at a time — see `requestSync`.
const syncTimeoutMs = 5_000;

// The loading barrier holds the whole room, so a client that cannot buffer has
// to answer late rather than never. Long enough for a slow connection to reach
// `canplaythrough`, short enough that one bad client is not everyone's problem.
const loadTimeoutMs = 15_000;

function toSearchResult(item: RoomQueueItem): SearchResult {
	return {
		id: item.id,
		// search results are normalised server-side, queue items are not
		name: item.originalTitle ?? item.name ?? 'Unknown title',
		artist: item.originalArtist ?? item.artist ?? 'Unknown artist',
		album: item.album ?? undefined,
		duration: item.duration,
		thumbnailUrl: item.thumbnailUrl
	};
}

/**
 * `<seconds> [<serverUtcMs>]`, which is what every frame that moves the shared
 * clock now carries. The stamp is optional on purpose: against a server that does
 * not send one the parse yields `NaN` and the clock falls back to half a round
 * trip, which is what it did before the stamps existed.
 */
function timedFrame(argument: string) {
	const [seconds, stamp] = argument.trim().split(/\s+/);
	return { seconds: Number(seconds), stamp: Number(stamp) };
}

let nextLineId = 0;

class Session {
	roomId: string | null = $state(null);
	joinedAs: string = $state('');
	/** What a `room name` frame said. The server confirms a rename to its sender
	 *  only, so for everybody else this stays empty — see `name`. */
	private named: string = $state('');
	description: string = $state('');
	status: SessionStatus = $state('offline');
	chat: ChatLine[] = $state([]);
	/** How many are in the room, this client included, as the room last said.
	 *  Zero until it says, which is how the strip knows to leave it out. */
	members: number = $state(0);
	unread: number = $state(0);
	chatOpen: boolean = $state(false);
	/** The room that went. A well-formed but unknown room GUID is accepted, then
	 *  closed with no frames. Zero frames received is the only "room does not
	 *  exist" signal. Kept as the id, not a flag, because `roomId` is cleared with
	 *  it: the room page needs to know which room not to join again. */
	gone: string | null = $state(null);

	// The clock's readouts, mirrored out of `SyncClock` rather than read through
	// getters: it is a plain class, so nothing about mutating it is reactive, and
	// a getter over it would leave the strip showing whatever it first rendered.

	/** How far behind the room this client is, in milliseconds. Positive means
	 *  the room is ahead and the player is being sped up to catch it. */
	offsetMs: number = $state(0);
	/** Round trip to the server, in milliseconds — the quickest seen lately,
	 *  which is the one the offset is actually derived from. */
	pingMs: number = $state(0);
	/** This device's audio clock against the server's, in parts per million,
	 *  positive when it runs fast. `null` until there is enough history to say —
	 *  which takes minutes, not seconds. */
	driftPpm: number | null = $state(null);

	private socket: WebSocket | null = null;
	private pending: string[] = [];
	private attempts = 0;
	private frames = 0;
	/** The track we owe the room a `loaded` for, once we can really play it. */
	private pendingFor: number | null = $state(null);
	private endedFor: number | null = null;
	private currentTrackId = '';
	/** The index this client has actually positioned playback on. Survives a
	 *  reconnect on purpose — it is what tells a replayed `current` frame apart
	 *  from a real one. `currentTrackId` cannot: `setQueue` writes that too. */
	private positionedAt: number | null = null;
	private awaitingBarrier = false;
	private closing = false;
	/** The room's clock, and this client's standing on it. */
	private clock = new SyncClock();
	private syncSentAt = 0;
	private syncInFlight = false;
	private syncTimer: ReturnType<typeof setInterval> | null = null;
	private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
	private loadTimer: ReturnType<typeof setTimeout> | null = null;

	/** The room's title. A rename reaches non-senders only through the lobby feed,
	 *  which the layout keeps open for the whole session, so that is the source of
	 *  truth; the frame is this client's own echo, which is all there is until the
	 *  feed lands. Empty for a room nobody has named — its name is its GUID. */
	get name() {
		const listed = rooms.list.find((room) => room.roomID === this.roomId);
		if (listed && !isUnnamed(listed)) return roomLabel(listed);
		return this.named;
	}

	get inRoom() {
		return this.roomId !== null;
	}

	/** True while this client owes the room a `loaded` for the current track. */
	get awaitingLoad() {
		return this.pendingFor !== null;
	}

	/** Commands to run as soon as the next connection opens — how a freshly
	 *  created room gets its name, since `CreateRoom` takes no body. */
	prime(commands: string[]) {
		this.pending.push(...commands);
	}

	connect(roomId: string, username: string) {
		this.teardown();
		this.closing = false;
		this.attempts = 0;
		this.roomId = roomId;
		this.joinedAs = username;
		this.gone = null;
		this.named = '';
		this.description = '';
		this.chat = [];
		this.members = 0;
		this.unread = 0;
		this.currentTrackId = '';
		this.positionedAt = null;
		queue.items = [];
		queue.currentIndex = 0;
		current.clear();
		audio.paused = true;
		audio.currentSeconds = 0;
		this.rewind();
		// set before the socket opens so a click in the meantime queues a command
		// instead of quietly mutating a queue the server owns
		queue.remote = (command) => this.send(command);
		this.open();
	}

	disconnect() {
		this.closing = true;
		this.teardown();
		this.pending = [];
		this.roomId = null;
		this.joinedAs = '';
		this.status = 'offline';
		this.awaitingBarrier = false;
		this.pendingFor = null;
		this.positionedAt = null;
		this.rewind();
		queue.remote = null;
		// The queue and what is playing stay: leaving the room hands the list back
		// to this client rather than throwing it away. The server owned it while
		// `remote` was set; with that null every verb is local again.
	}

	send(command: string) {
		if (this.socket?.readyState === WebSocket.OPEN) return this.socket.send(command);

		// Before the room's first connection there is nothing to be out of step
		// with, so a click in that window is worth holding — that is what `prime`
		// and the pre-open `queue.remote` rely on. After a drop it is the opposite:
		// the command describes a moment that has passed by the time the socket
		// comes back, and flushing a stranded `next`, `playpause` or barrier answer
		// on reconnect moves the room for everybody. That is the desync one
		// dropped client hands to the rest, so those are dropped instead.
		if (this.attempts > 0) return;
		this.pending.push(command);
	}

	/** Answers the loading barrier, once the player can really play the track.
	 *  Both barriers count messages, not distinct users, so a second `loaded`
	 *  releases the barrier early for everybody — hence the pending guard, which
	 *  also makes this a no-op to call when nothing is waiting on it. */
	reportLoaded() {
		if (this.pendingFor === null) return;
		this.pendingFor = null;
		this.clearTimer('loadTimer');
		this.send('loaded');
	}

	/** Sent exactly once per track. Both barriers count messages, not distinct
	 *  users, so a second `end` releases the barrier early for everybody. */
	reportEnded() {
		if (this.endedFor === queue.currentIndex) return;
		this.endedFor = queue.currentIndex;
		this.send('end');
	}

	/**
	 * The player gave up on the current track. Sat out, not skipped: the room plays on
	 * for everyone who has it, so this never sends `next`. Both barriers still count
	 * this client, so it answers both now — or the room waits on it at the top of the
	 * track, and again at the end of it for a sound that never started here. When the
	 * file is broken for everyone, everyone answers `end` this way, and that is what
	 * moves the room on.
	 *
	 * ponytail: a room-wide outage walks the room's queue one barrier at a time. Hold
	 * `end` after a streak, the way playing alone does, if that ever bites.
	 */
	giveUp(reason: string) {
		this.reportLoaded();
		// `src=""` fails too, and an `end` sent with nothing playing is a vote for a
		// barrier nobody armed
		const track = queue.items[queue.currentIndex];
		if (!current.url || !track) return;
		skipped.add(track, reason);
		this.reportEnded();
	}

	/** The strip's resync button. Drops the position window so the next reading
	 *  is allowed its opening snap — the same path a track change goes down —
	 *  and asks for one now instead of waiting out the settled spacing. */
	resync() {
		if (!this.inRoom) return;
		this.rewind();
		this.requestSync();
	}

	rename(name: string) {
		const trimmed = name.trim();
		if (!trimmed) return;
		this.send(`updateroom name ${trimmed}`);
	}

	private open() {
		this.status = this.attempts === 0 ? 'connecting' : 'reconnecting';
		this.frames = 0;
		// `endedFor` and the barrier state deliberately survive a reconnect: the
		// server arms each barrier once per track change and counts raw messages,
		// so answering again after a rejoin is an extra vote, not a replacement.
		this.pendingFor = null;
		this.clearTimer('loadTimer');

		let query = `room=${this.roomId}`;
		if (this.joinedAs) query += `&username=${encodeURIComponent(this.joinedAs)}`;

		const socket = new WebSocket(audioWsUrl(`/Multiplayer/Join?${query}`));
		this.socket = socket;

		socket.onopen = () => {
			this.attempts = 0;
			for (const command of this.pending.splice(0)) socket.send(command);
			this.syncTimer = setInterval(() => this.requestSync(), minSyncSpacingMs);
		};
		socket.onmessage = (event) => {
			this.frames++;
			this.receive(String(event.data));
		};
		socket.onclose = () => this.closed();
	}

	private closed() {
		this.clearTimer('syncTimer');
		this.syncInFlight = false;
		this.socket = null;
		if (this.closing || this.roomId === null) return;

		if (this.frames === 0) {
			this.gone = this.roomId;
			this.roomId = null;
			this.status = 'offline';
			queue.remote = null;
			return;
		}

		this.status = 'reconnecting';
		this.attempts++;
		this.reconnectTimer = setTimeout(() => this.open(), Math.min(1000 * 2 ** (this.attempts - 1), 30_000));
	}

	private receive(raw: string) {
		const space = raw.indexOf(' ');
		const command = space === -1 ? raw : raw.slice(0, space);
		const argument = space === -1 ? '' : raw.slice(space + 1);

		switch (command) {
			case 'queue':
				this.setQueue(argument);
				break;
			case 'current':
				this.setCurrent(Number(argument));
				break;
			case 'index':
				this.reindex(Number(argument));
				break;
			case 'playing':
				audio.paused = argument.trim() !== 'True';
				if (!audio.paused) {
					this.awaitingBarrier = false;
					this.status = 'synced';
				} else this.status = this.awaitingBarrier ? 'holding' : 'paused';
				break;
			case 'seek':
				this.placeAt(timedFrame(argument));
				break;
			case 'sync': {
				const { seconds, stamp } = timedFrame(argument);
				this.applySync(seconds, stamp);
				break;
			}
			case 'stop':
				audio.paused = true;
				this.awaitingBarrier = false;
				this.status = 'stopped';
				break;
			case 'chat':
				this.addChat(argument);
				break;
			case 'room':
				this.setRoomField(argument);
				break;
			case 'members':
				this.members = Number(argument) || 0;
				break;
		}
	}

	private setQueue(json: string) {
		let items: RoomQueueItem[];
		try {
			items = JSON.parse(json);
		} catch {
			return;
		}
		queue.items = proxyThumbnails(items.map(toSearchResult));

		// `remove` shifts `current` implicitly and broadcasts only a queue, and the
		// first `add` to an idle room lands the same way — so which track is
		// current has to be re-derived after every list, down the same path a
		// `current` frame takes, loading barrier and all. Deriving it here instead
		// would claim the track and leave the `current` that follows looking like a
		// replay, which is a barrier nobody ever answers.
		this.setCurrent(queue.currentIndex);
	}

	/**
	 * A reorder moved the current track without changing it — shuffle, clear, move, or a
	 * removal below the current index. It leads the `queue` frame that follows, because
	 * that frame is read against this index: arriving second, it would be read against the
	 * old one, which now names a different track and restarts a track nobody changed.
	 *
	 * Deliberately not `current`: no barrier, no rewind, nothing stops the audio.
	 */
	private reindex(index: number) {
		if (!Number.isFinite(index)) return;
		queue.currentIndex = index;
		if (this.positionedAt !== null) this.positionedAt = index;
	}

	private setCurrent(index: number) {
		if (!Number.isFinite(index)) return;

		// `next` can move current one past the end, which means nothing is playing
		const now = queue.items[index];
		// Rejoining replays the room's whole state, `current` included. Only a real
		// change may touch playback: treating the replay as one restarts the track
		// from zero and holds it there, which is a client desyncing itself the
		// moment it recovers. `Joined` sends a `seek` right after, which puts a
		// rejoining client back on the room's clock.
		const changed = index !== this.positionedAt || (now?.id ?? '') !== this.currentTrackId;

		if (changed) {
			this.positionedAt = index;
			queue.currentIndex = index;
			audio.paused = true;
			audio.currentSeconds = 0;
			this.rewind();
			this.awaitingBarrier = true;
			this.status = 'holding';
			this.endedFor = null;

			if (now) {
				this.currentTrackId = now.id;
				current.set(now);
			} else {
				this.currentTrackId = '';
				current.clear();
			}
		}

		// Only a real change arms the barrier, because that is the only thing that
		// arms it server-side too — `UpdateStart` runs in next/previous/skipto and
		// nowhere else, and `Joined` replays the room's state without expecting an
		// answer. `LoadedCount` is a raw tally against the live user count, so an
		// answer to a replayed `current` is an extra vote that releases the next
		// barrier early for everybody.
		if (!changed) return;
		this.pendingFor = index;

		// nothing to buffer, but the server still counts this client against every
		// member before it starts the clock again
		if (!now) return this.reportLoaded();

		// the player answers this once it can really play the track — a `current`
		// frame only means the URL changed. The timer is the floor under a client
		// that never gets there, so one stalled buffer cannot hold the room shut.
		this.clearTimer('loadTimer');
		this.loadTimer = setTimeout(() => this.reportLoaded(), loadTimeoutMs);
	}

	/**
	 * A `seek` frame: where the room is, and when it was there.
	 *
	 * The position was measured when the server broadcast it, so it is one downlink
	 * stale by the time it lands here. The stamp says exactly how stale; without one
	 * the best available guess is half a round trip, which is only right on a
	 * symmetric path.
	 */
	private placeAt({ seconds, stamp }: { seconds: number; stamp: number }) {
		if (!Number.isFinite(seconds)) return;
		audio.currentSeconds = seconds + this.clock.stalenessOf(stamp, performance.now());
	}

	/**
	 * One reading of the room's clock, and the correction it buys.
	 *
	 * The reply is stale by however long the return trip took, which is why this
	 * cannot compare against the raw number: on a symmetric path a client's own
	 * lateness and the reply's staleness are the same size and cancel, so a
	 * client a tenth of a second behind the room measures itself as fine. The
	 * `SyncClock` credits the trip back; everything here does is spend the result.
	 */
	private applySync(reported: number, serverUtcMs: number) {
		this.syncInFlight = false;
		if (!Number.isFinite(reported)) return;

		const error = this.clock.sample(this.syncSentAt, performance.now(), reported, audio.positionNow(), serverUtcMs);

		if (this.clock.shouldSeek(error)) {
			// too far out for the rate budget to close, or the track's opening
			// reading, where a jump costs nothing and steering costs a slow arrival
			audio.currentSeconds = audio.positionNow() + error;
			audio.rate = 1;
			this.clock.seeked();
		} else {
			audio.rate = this.clock.rateFor(error);
		}

		this.offsetMs = Math.round(error * 1000);
		this.pingMs = Math.round(this.clock.halfRtt * 2000);
		const drift = this.clock.drift;
		this.driftPpm = drift === null ? null : Math.round(drift * 1e6);

		// the word on the strip now follows the measurement rather than a timeout
		if (this.awaitingBarrier || audio.paused) return;
		this.status = this.converging ? 'catching up' : 'synced';
	}

	/**
	 * Whether this client is still chasing the room: either the loop has hold of
	 * the rate, or the last reading was outside the band and it is about to. Drives
	 * both the word on the strip and how hard to poll, so the two cannot disagree —
	 * and the loop's own latch does the hysteresis, so neither can flap.
	 */
	private get converging() {
		return this.clock.steering || Math.abs(this.offsetMs) > this.clock.deadband * 1000;
	}

	/**
	 * Asks the room where it is. Replies carry no request id, so a second request
	 * in flight would be answered by the first reply and time a 600 ms link at
	 * 1 ms — hence one at a time, self-clocked off the answers rather than a fixed
	 * interval. This is sent past `send`, because a `sync` stranded by a dropped
	 * socket describes a moment that has passed and must not be flushed later.
	 */
	private requestSync() {
		if (this.syncInFlight) {
			if (performance.now() - this.syncSentAt < syncTimeoutMs) return;
			this.syncInFlight = false;
		}
		if (performance.now() - this.syncSentAt < this.syncSpacing) return;
		if (this.socket?.readyState !== WebSocket.OPEN) return;

		this.syncInFlight = true;
		this.syncSentAt = performance.now();
		this.socket.send('sync');
	}

	/**
	 * How hard to poll. Four times a second is what *converging* costs, not what
	 * holding costs: a track that has had its opening correction and is sitting
	 * inside the deadband has nothing left to chase but the device's own drift,
	 * which moves a few milliseconds a minute. Every real disturbance — a seek, a
	 * pause, a resume, a track change — arrives as a stamped frame that places
	 * itself without waiting for a poll, and resets `settled` on the way through.
	 */
	private get syncSpacing() {
		return this.clock.settled && !this.converging ? settledSyncSpacingMs : minSyncSpacingMs;
	}

	/** A track change, a join, a leave: the samples describe a position that no
	 *  longer exists. What the clock knows about the *link*, and about this
	 *  device's drift, survives — neither of those changed. */
	private rewind() {
		this.clock.reset();
		this.offsetMs = 0;
		audio.rate = 1;
		// `pingMs` and `driftPpm` stay: the link and the device are what they were
	}

	private addChat(argument: string) {
		// split on the first ` %% ` only: the username is an unvalidated query
		// parameter and the text may contain another separator
		const separator = argument.indexOf(' %% ');
		const username = (separator === -1 ? argument : argument.slice(0, separator)).trim();
		const text = (separator === -1 ? '' : argument.slice(separator + 4)).trim();
		const system = username === 'System';

		const line: ChatLine = {
			id: nextLineId++,
			username,
			text,
			system,
			at: new Date().toLocaleTimeString([], {
				hour: '2-digit',
				minute: '2-digit'
			})
		};
		this.chat = [...this.chat, line].slice(-maximumChatLines);
		if (!this.chatOpen) this.unread++;
	}

	private setRoomField(argument: string) {
		const space = argument.indexOf(' ');
		if (space === -1) return;
		const field = argument.slice(0, space);
		const value = argument.slice(space + 1).trim();

		if (field === 'name') this.named = value;
		else if (field === 'description') this.description = value;
	}

	private clearTimer(which: 'syncTimer' | 'reconnectTimer' | 'loadTimer') {
		const timer = this[which];
		if (timer === null) return;
		clearTimeout(timer as ReturnType<typeof setTimeout>);
		clearInterval(timer as ReturnType<typeof setInterval>);
		this[which] = null;
	}

	/**
	 * The page is going away, maybe into the back/forward cache. Chrome freezes a cached
	 * page with its socket still open and keeps answering the server's pings for it, so
	 * the room holds a member who has left, and both barriers wait on them until the
	 * cache lets the page go. Closed here, it is a dropped connection like any other:
	 * `closed` queues the rejoin, which runs if the page ever comes back.
	 */
	leavePage() {
		this.socket?.close();
	}

	/** Drops the socket and its timers without touching primed commands. */
	private teardown() {
		this.clearTimer('syncTimer');
		this.clearTimer('reconnectTimer');
		this.clearTimer('loadTimer');
		if (!this.socket) return;
		this.socket.onclose = null;
		this.socket.close();
		this.socket = null;
	}
}

const session = new Session();

// absent during server rendering, which has no page to leave
globalThis.addEventListener?.('pagehide', () => session.leavePage());

export default session;
