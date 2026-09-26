import { login, logout, me, register, type Session } from '$requests/accounts';
import { AudioApiError } from '$requests/songs';
import user from './user.svelte';

const storageKey = 'musicrain.token';

/** Absent while prerendering, and in a browser that refuses storage. Either way: no session. */
const storage = () => (typeof localStorage === 'undefined' ? null : localStorage);

/**
 * Who you are signed in as, and the bearer token that says so.
 *
 * Deliberately separate from `user.svelte` — that one is a name for a room socket,
 * chosen per browser profile, and an account is a thing on a server. Signing in
 * offers the account's name to the room identity; it does not replace it.
 */
class Account {
	username: string | null = $state(null);
	token: string | null = $state(null);
	/** Set by the panel while a request is in flight, so the button can say so. */
	busy = $state(false);
	error = $state('');

	get signedIn() {
		return this.token !== null;
	}

	load() {
		const stored = storage()?.getItem(storageKey);
		if (!stored) return;

		try {
			const session = JSON.parse(stored) as Session;
			// a token past the expiry stored here is a token nothing has used since —
			// nothing slid it, so it really is dead. Sign the panel out before it makes
			// a request that can only fail.
			if (Date.parse(session.expiresUtc) <= Date.now()) return this.forget();

			this.username = session.username;
			this.token = session.token;
			this.renew(session);
		} catch {
			this.forget();
		}
	}

	/**
	 * Dom slides the expiry forward on every authenticated request, so what a sign-in
	 * wrote down is a floor rather than the truth — a session kept in daily use would
	 * otherwise sign itself out thirty days after the sign-in that started it. `Me`
	 * says where the expiry really stands now; a 401 says the token is gone for good.
	 */
	private async renew(session: Session) {
		try {
			const { expiresUtc } = await me(session.token);
			if (this.token === session.token)
				storage()?.setItem(storageKey, JSON.stringify({ ...session, expiresUtc }));
		} catch (error) {
			// anything else — Dom down, no network — leaves the stored session alone,
			// because being offline is not being signed out
			if (error instanceof AudioApiError) this.reject(error.status);
		}
	}

	signUp(username: string, password: string) {
		return this.attempt(() => register(username, password));
	}

	signIn(username: string, password: string) {
		return this.attempt(() => login(username, password));
	}

	/**
	 * What an authenticated call does with a 401. The expiry is only read once, at
	 * `load`, so a session that runs out while the tab is open — or a token revoked
	 * from somewhere else — otherwise leaves the panel signed in over a token every
	 * request refuses. Dropping it here is what turns that into a sign-in prompt.
	 */
	reject(status: number) {
		if (status === 401 && this.token) this.forget();
	}

	/** Best effort: the token is dropped here whether or not Dom heard about it. */
	async signOut() {
		const token = this.token;
		this.forget();
		if (token) await logout(token).catch(() => undefined);
	}

	private async attempt(request: () => Promise<Session>) {
		this.busy = true;
		this.error = '';
		try {
			this.keep(await request());
			return true;
		} catch (error) {
			this.error = error instanceof Error ? error.message : 'Could not reach the audio service.';
			return false;
		} finally {
			this.busy = false;
		}
	}

	private keep(session: Session) {
		this.username = session.username;
		this.token = session.token;
		storage()?.setItem(storageKey, JSON.stringify(session));

		// The room identity is a separate thing, but nobody wants to be `kris` here and
		// `Anonymous 4` in a room. Discord's own name still wins inside the activity.
		if (user.source !== 'discord') user.choose(session.username);
	}

	private forget() {
		this.username = null;
		this.token = null;
		storage()?.removeItem(storageKey);
	}
}

export default new Account();
