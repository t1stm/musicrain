import { untrack } from 'svelte';
import { getSettings, patchSettings } from '$requests/accounts';
import { AudioApiError } from '$requests/songs';
import account from './account.svelte';
import lyrics from './lyrics.svelte';
import quality, { bitrates, codecs, type Bitrate, type Codec } from './quality.svelte';
import user from './user.svelte';

const storageKey = 'musicrain.settings';

/** One burst of changes — a finger run along the bitrate ladder — is one request. */
const pushDelayMs = 1000;

type Field<T> = {
	/** `undefined` when the value on screen is not this device's to keep: a Discord name. */
	get(): T | undefined;
	set(value: T): void;
	/** Everything read back from Dom or from storage passes through here first. */
	valid(value: unknown): value is T;
	/** Whether one device may keep its own. */
	deviceOnlyAllowed: boolean;
};

type Quality = { codec: Codec; bitrate: Bitrate };

/**
 * Every preference there is, each pointed at the state that already owns it — so the
 * player's quality popover, the lyrics button and the room's name gate keep reading and
 * writing what they always have, and this store follows along.
 */
const fields = {
	quality: {
		get: () => ({ codec: quality.codec, bitrate: quality.bitrate }),
		set: (value) => {
			quality.codec = value.codec;
			quality.bitrate = value.bitrate;
		},
		valid: (value): value is Quality =>
			typeof value === 'object' &&
			value !== null &&
			codecs.includes((value as Quality).codec) &&
			bitrates.includes((value as Quality).bitrate),
		deviceOnlyAllowed: true,
	} satisfies Field<Quality>,
	chatName: {
		get: () => (user.source === 'discord' ? undefined : user.username),
		set: (value) => {
			if (user.source !== 'discord') user.username = value;
		},
		valid: (value): value is string | null =>
			value === null || (typeof value === 'string' && value.length <= 60),
		deviceOnlyAllowed: true,
	} satisfies Field<string | null>,
	lyricsOpen: {
		get: () => lyrics.open,
		set: (value) => (lyrics.open = value),
		valid: (value): value is boolean => typeof value === 'boolean',
		deviceOnlyAllowed: false,
	} satisfies Field<boolean>,
};

export type SettingKey = keyof typeof fields;

const keys = Object.keys(fields) as SettingKey[];
const field = (key: SettingKey) => fields[key] as unknown as Field<unknown>;
const isKey = (key: unknown): key is SettingKey => typeof key === 'string' && key in fields;

type Stored = {
	/** This device's value for every key, the device-only ones included. */
	values: Partial<Record<SettingKey, unknown>>;
	deviceOnly: SettingKey[];
	/** Changed while signed in, and not yet acknowledged by Dom. */
	dirty: SettingKey[];
};

/**
 * The account's preferences, mirrored on this device.
 *
 * Dom holds one settings object per account and merges what it is sent key by key. This
 * store reads it on every load and on every sign-in, and sends each change a second after
 * it settles. The rules, in the order they are applied:
 *
 * - A device-only key is never sent, and never overwritten by the account's value.
 * - A key changed here while signed in, and not yet acknowledged, is newer than the
 *   account's copy: it is sent, not overwritten. That is an edit made offline.
 * - Otherwise the account wins. Changes made while signed out never count as newer, so
 *   signing in replaces them.
 * - A key the account has never saved is filled from this device.
 */
class Settings {
	deviceOnly: SettingKey[] = $state([]);

	#dirty = new Set<SettingKey>();
	/** Each value as last saved or applied, as JSON: what a change is measured against. */
	#known: Partial<Record<SettingKey, string>> = {};
	#signedIn = false;
	#timer: ReturnType<typeof setTimeout> | undefined;
	#stop: (() => void) | null = null;

	isDeviceOnly(key: SettingKey) {
		return this.deviceOnly.includes(key);
	}

	/** Restores this device's values, then follows every change and the account. Browser only. */
	load() {
		if (this.#stop) return;

		const stored = read();
		this.deviceOnly = stored.deviceOnly.filter((key) => fields[key].deviceOnlyAllowed);
		this.#dirty = new Set(stored.dirty);

		for (const key of keys) {
			const value = stored.values[key];
			if (value !== undefined && field(key).valid(value)) field(key).set(value);
			this.#known[key] = json(field(key).get());
		}

		this.#stop = $effect.root(() => {
			$effect(() => this.#follow(keys.map((key) => field(key).get())));
			$effect(() => this.#account(account.signedIn));
		});
	}

	/**
	 * Keeping a key to this device stops it syncing and keeps its value. Syncing it again is
	 * a sign-in in miniature: the account's value replaces this one, or this one fills it.
	 */
	setDeviceOnly(key: SettingKey, on: boolean) {
		if (!fields[key].deviceOnlyAllowed || on === this.isDeviceOnly(key)) return;

		if (on) {
			this.deviceOnly = [...this.deviceOnly, key];
			this.#dirty.delete(key);
			this.#save();
			return;
		}

		this.deviceOnly = this.deviceOnly.filter((kept) => kept !== key);
		this.#save();
		void this.#pull();
	}

	#follow(values: unknown[]) {
		untrack(() => {
			let changed = false;

			keys.forEach((key, index) => {
				const now = json(values[index]);
				if (now === undefined || now === this.#known[key]) return;

				this.#known[key] = now;
				if (account.signedIn && !this.isDeviceOnly(key)) this.#dirty.add(key);
				changed = true;
			});

			if (!changed) return;
			this.#save();
			this.#schedule();
		});
	}

	#account(signedIn: boolean) {
		untrack(() => {
			if (signedIn && !this.#signedIn) void this.#pull();

			// what was waiting to go to the old account is not the next account's business
			if (!signedIn && this.#dirty.size > 0) {
				this.#dirty.clear();
				this.#save();
			}

			this.#signedIn = signedIn;
		});
	}

	async #pull() {
		const token = account.token;
		if (!token) return;

		let saved: Record<string, unknown> | null;
		try {
			({ settings: saved } = await getSettings(token));
		} catch (error) {
			return this.#failed(error);
		}
		if (account.token !== token) return;

		const remote = saved ?? {};
		for (const key of keys) {
			if (this.isDeviceOnly(key) || this.#dirty.has(key)) continue;

			// A value this version cannot read is left where it is, on both sides: it may be
			// something a newer tab saved, and sending ours over it would lose it.
			if (key in remote) {
				if (field(key).valid(remote[key])) this.#apply(key, remote[key]);
				continue;
			}

			// Nobody wants to be `kris` here and `Anonymous 4` in a room, so an account with no
			// chat name yet starts from its own name. Discord's name still wins inside the activity.
			if (key === 'chatName' && user.source !== 'discord' && account.username)
				this.#apply(key, account.username);

			if (this.#known[key] !== undefined && this.#known[key] !== 'null') this.#dirty.add(key);
		}

		this.#save();
		await this.#push();
	}

	#apply(key: SettingKey, value: unknown) {
		field(key).set(value);
		// read back rather than taken as sent: the same value in a different key order is not a change
		this.#known[key] = json(field(key).get()) ?? json(value);
	}

	#pending() {
		return [...this.#dirty].filter((key) => !this.isDeviceOnly(key));
	}

	#schedule() {
		clearTimeout(this.#timer);
		if (account.signedIn && this.#pending().length > 0)
			this.#timer = setTimeout(() => void this.#push(), pushDelayMs);
	}

	async #push() {
		clearTimeout(this.#timer);

		const token = account.token;
		const pending = this.#pending();
		if (!token || pending.length === 0) return;

		const sent = pending.map((key) => [key, this.#known[key]] as const);
		try {
			await patchSettings(
				token,
				Object.fromEntries(sent.map(([key, value]) => [key, JSON.parse(value ?? 'null')])),
			);
		} catch (error) {
			return this.#failed(error);
		}

		// a key changed again while this was in flight stays dirty for the next push
		for (const [key, value] of sent) if (this.#known[key] === value) this.#dirty.delete(key);
		this.#save();
	}

	/** A 401 means the token is gone. Anything else — no network, Dom down — waits for the next try. */
	#failed(error: unknown) {
		if (error instanceof AudioApiError) account.reject(error.status);
	}

	#save() {
		const values = Object.fromEntries(
			keys.flatMap((key) => {
				const known = this.#known[key];
				return known === undefined ? [] : [[key, JSON.parse(known)]];
			}),
		);

		try {
			localStorage.setItem(
				storageKey,
				JSON.stringify({ values, deviceOnly: this.deviceOnly, dirty: [...this.#dirty] }),
			);
		} catch {
			// A preference that cannot be remembered is still a preference for this session.
		}
	}
}

/** `undefined` stays `undefined`, where `JSON.stringify` would make it a string for some inputs. */
const json = (value: unknown) => (value === undefined ? undefined : JSON.stringify(value));

// Server-side rendering, a private window and blocked site data all fail the same way — by
// throwing on access — so the try/catch is the guard for all three.
function read(): Stored {
	try {
		const raw = localStorage.getItem(storageKey);
		if (raw !== null) {
			const stored = JSON.parse(raw) as Partial<Stored>;
			return {
				values: typeof stored.values === 'object' && stored.values !== null ? stored.values : {},
				deviceOnly: Array.isArray(stored.deviceOnly) ? stored.deviceOnly.filter(isKey) : [],
				dirty: Array.isArray(stored.dirty) ? stored.dirty.filter(isKey) : [],
			};
		}

		// The first load since this store existed: carry over what the old keys remembered.
		const values: Stored['values'] = {};
		const name = localStorage.getItem('musicrain.username');
		if (name !== null) values.chatName = name;
		const open = localStorage.getItem('musicrain.lyrics-open');
		if (open !== null) values.lyricsOpen = open === 'true';

		return { values, deviceOnly: [], dirty: [] };
	} catch {
		return { values: {}, deviceOnly: [], dirty: [] };
	}
}

export default new Settings();
