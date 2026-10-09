import { untrack } from 'svelte';
import { convertTimeSpanStringToSeconds } from '$lib';
import { isDiscordActivity } from '$lib/discord';
import {
	claimHistory,
	putPlay,
	uuid,
	type EndReason,
	type PlayBody,
	type StartReason,
} from '$requests/history';
import type { SearchResult } from '$states/search.svelte';
import account from './account.svelte';
import audio from './audio.svelte';

const outboxKey = 'musicrain.history-outbox';
const sessionKey = 'musicrain.history-session';

/** A play starting this long after the last one was heard from opens a new session. */
const sessionGapMs = 30 * 60_000;
/** What the history views count as a play, so the one write in the middle of one. */
const countsMs = 30_000;
const outboxLimit = 200;
/** Moliv refuses a play older than this, so the outbox stops trying. */
const outboxHorizonMs = 30 * 86_400_000;

/**
 * How the previous play ends when the next one begins over it, by how the next began.
 * A track that ran out ended itself already (`end('finished')`), so nothing is open by
 * then; `autoplay` only finds one open when the current track was taken out of the queue.
 */
const endedBy: Record<StartReason, EndReason> = {
	chosen: 'replaced',
	collection: 'replaced',
	autoplay: 'replaced',
	next: 'skipped',
	previous: 'previous',
	// the room moved on before this client heard the end
	room: 'skipped',
};

/** The play in progress. `heard` is seconds, kept fractional; the body carries whole ms. */
type Open = { id: string; body: PlayBody; account: string | null; heard: number; counted: boolean };
/** A play the server has not confirmed: its last body, and whose it was. */
type Entry = { body: PlayBody; account: string | null };
type Outbox = Record<string, Entry>;

const storage = () => (typeof localStorage === 'undefined' ? null : localStorage);
const matches = (query: string) => typeof matchMedia === 'function' && matchMedia(query).matches;

function read<T>(key: string): T | null {
	try {
		return JSON.parse(storage()?.getItem(key) ?? 'null') as T | null;
	} catch {
		return null;
	}
}

function write(key: string, value: unknown) {
	try {
		storage()?.setItem(key, JSON.stringify(value));
	} catch {
		// a full or refused storage loses the resend, not the play: the PUT is still made
	}
}

/**
 * Whose plays these are. ponytail: the username, so a rename drops whatever the outbox still
 * held from before it; key it by Dom's account id if that ever loses anything worth keeping.
 */
const owner = () => account.username?.toLowerCase() ?? null;

/**
 * The entries still worth sending, as the caller is now. A play recorded under another
 * account is dropped rather than sent — a shared device never files one person's plays
 * under the next. An anonymous one goes to whoever is signed in now, which is what a
 * claim would have done with it anyway.
 */
export function prune(outbox: Outbox, signedIn: string | null, now: number): Outbox {
	const kept = Object.entries(outbox)
		.filter(([, entry]) => entry.account === null || entry.account === signedIn)
		.filter(([, entry]) => now - Date.parse(entry.body.startedUtc) < outboxHorizonMs)
		// oldest first, so they arrive in the order they were played; past the cap the oldest go
		.sort(([, a], [, b]) => a.body.startedUtc.localeCompare(b.body.startedUtc))
		.slice(-outboxLimit);
	return Object.fromEntries(kept);
}

/**
 * The listening history's half on this side: what is playing, how much of it was heard, and
 * every play the server has not confirmed yet. One play at a time — the queue and the room
 * say when one begins and ends, and the players feed it the position.
 */
class History {
	/** Settings → Playback → Record plays, the other way round. While set, nothing new is recorded. */
	paused = $state(false);

	#play: Open | null = null;
	/** The position at the last tick: a jump from it is a seek, standing still a pause. */
	#last = 0;
	#flushing: Promise<void> | null = null;
	#again = false;

	/** Follows the position and the account, and sends on the way out. Browser only, once. */
	init() {
		$effect.root(() => {
			$effect(() => {
				const seconds = audio.currentSeconds;
				untrack(() => this.tick(seconds));
			});
			// a sign-in moves this device's anonymous plays onto the account, and whatever the
			// outbox held goes out under whoever is signed in now — on load too
			$effect(() => {
				const token = account.token;
				untrack(() => {
					if (token) void claimHistory(token).catch(() => undefined);
					void this.flush();
				});
			});
		});

		// A phone with the screen off is hidden and still playing, so this is progress only,
		// never an end — the end is whatever comes next, or nothing if the tab is closing.
		const hidden = () => this.#send(this.#play, true);
		addEventListener('pagehide', hidden);
		document.addEventListener('visibilitychange', () => {
			if (document.visibilityState === 'hidden') hidden();
		});
	}

	/**
	 * A new track started. Whatever was open ends with the reason that follows from how this
	 * one began, unless the caller knows better.
	 */
	begin(track: SearchResult, reason: StartReason, shuffled = false) {
		if (this.#play) this.end(endedBy[reason]);
		this.#last = 0;
		if (this.paused || !storage()) return;

		const now = new Date();
		const origin = reason === 'room' ? { kind: 'room' as const } : track.origin;
		const durationMs = Math.round(convertTimeSpanStringToSeconds(track.duration ?? '') * 1000);

		this.#play = {
			id: uuid(),
			account: owner(),
			heard: 0,
			counted: false,
			body: {
				trackId: track.id,
				startedUtc: now.toISOString(),
				// getTimezoneOffset is minutes *behind* UTC, so Sofia in summer is -180
				utcOffsetMinutes: -now.getTimezoneOffset(),
				durationMs: Number.isFinite(durationMs) && durationMs > 0 ? durationMs : 0,
				playedMs: 0,
				startReason: reason,
				endReason: null,
				sourceKind: origin?.kind ?? 'queue',
				sourceId: origin?.id ?? null,
				...this.#session(now.getTime()),
				platform: isDiscordActivity ? 'discord' : matches('(display-mode: standalone)') ? 'pwa' : 'web',
				deviceKind: matches('(pointer: coarse)') ? 'mobile' : 'desktop',
				shuffled,
			},
		};
		this.#send(this.#play);
	}

	/** The open play ended. A no-op when nothing is open — the end of a play nobody recorded. */
	end(reason: EndReason) {
		const play = this.#play;
		if (!play) return;
		this.#play = null;
		play.body.endReason = reason;
		this.#send(play);
	}

	/** Adds the time since the last tick, when it is the kind of gap playing makes. */
	tick(seconds: number) {
		const gap = seconds - this.#last;
		this.#last = seconds;

		const play = this.#play;
		if (!play || gap <= 0 || gap >= 3) return;

		play.heard += gap;
		if (!play.counted && play.heard * 1000 >= countsMs) {
			play.counted = true;
			this.#send(play);
		}
	}

	/**
	 * Sends what the outbox holds, oldest concern first: entries that are no longer this
	 * caller's go, and what is left is written. One drain at a time; a send during one asks
	 * for another pass, so the newest body of a play is always the last one sent.
	 */
	flush(): Promise<void> {
		if (this.#flushing) {
			this.#again = true;
			return this.#flushing;
		}

		this.#flushing = (async () => {
			do {
				this.#again = false;
				await this.#drain();
			} while (this.#again);
		})().finally(() => (this.#flushing = null));
		return this.#flushing;
	}

	async #drain() {
		const outbox = prune(read<Outbox>(outboxKey) ?? {}, owner(), Date.now());
		write(outboxKey, outbox);

		for (const [id, entry] of Object.entries(outbox)) {
			const status = await putPlay(id, entry.body, account.token);

			// Offline, Moliv down, Dom down: every other entry would answer the same, so they
			// all wait for the next send. A dead token signs out, and the entries wait for
			// whoever signs in next.
			if (status === 0 || status === 429 || status >= 500) return;
			if (status === 401) return account.reject(401);

			// Written, or refused for good — a 400 or a 403 says the same thing every time. Only
			// the body that was sent is done: a newer one written meanwhile stays to go next.
			const now = read<Outbox>(outboxKey) ?? {};
			if (JSON.stringify(now[id]?.body) === JSON.stringify(entry.body)) {
				delete now[id];
				write(outboxKey, now);
			}
		}
	}

	#send(play: Open | null, keepalive = false) {
		if (!play) return;
		play.body.playedMs = Math.round(play.heard * 1000);

		const outbox = read<Outbox>(outboxKey) ?? {};
		outbox[play.id] = { body: { ...play.body }, account: play.account };
		write(outboxKey, outbox);
		this.#heard(Date.now());

		// On the way out there is no awaiting anything: one request, kept alive past the page,
		// and the outbox sends it again on the next load if the browser dropped it.
		if (keepalive) void putPlay(play.id, play.body, account.token, true);
		else void this.flush();
	}

	/** The session this play belongs to, per device: two devices playing at once are two. */
	#session(now: number) {
		const stored = read<{ id: string; position: number; lastMs: number }>(sessionKey);
		const session =
			stored && now - stored.lastMs <= sessionGapMs
				? { id: stored.id, position: stored.position + 1 }
				: { id: uuid(), position: 0 };
		write(sessionKey, { ...session, lastMs: now });
		return { sessionId: session.id, sessionPosition: session.position };
	}

	/** Every write is activity: a long track is one play, not a gap that ends the session. */
	#heard(now: number) {
		const stored = read<{ id: string; position: number; lastMs: number }>(sessionKey);
		if (stored) write(sessionKey, { ...stored, lastMs: now });
	}
}

export default new History();
