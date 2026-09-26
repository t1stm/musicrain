import {
	changePassword,
	deleteAccount,
	login,
	logout,
	me,
	register,
	rename,
	signOutEverywhere,
	type Session,
} from '$requests/accounts';
import { AudioApiError } from '$requests/songs';

const storageKey = 'musicrain.token';

/** Absent while prerendering, and in a browser that refuses storage. Either way: no session. */
const storage = () => (typeof localStorage === 'undefined' ? null : localStorage);

/**
 * Who you are signed in as, and the bearer token that says so.
 *
 * Deliberately separate from `user.svelte` — that one is a name for a room socket,
 * and an account is a thing on a server. The chat name is one of the account's
 * settings (see `settings.svelte`), not the account's name.
 */
class Account {
	username: string | null = $state(null);
	token: string | null = $state(null);
	/** Set while a request is in flight, so the buttons can say so. */
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
			// nothing slid it, so it really is dead. Sign out before it makes
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
	 * from somewhere else — otherwise leaves the app signed in over a token every
	 * request refuses. Dropping it here is what turns that into a sign-in prompt.
	 */
	reject(status: number) {
		if (status === 401 && this.token) this.forget();
	}

	/** Every other device this account is signed in on. `value` is how many were signed out. */
	signOutEverywhere() {
		return this.change((token) => signOutEverywhere(token).then(({ revoked }) => revoked));
	}

	/** Dom revokes every token on a new password, this one too, and answers with a fresh one. */
	changePassword(current: string, password: string) {
		return this.change((token) =>
			changePassword(token, current, password).then((session) => this.keep(session)),
		);
	}

	rename(username: string, password: string) {
		return this.change((token) =>
			rename(token, username, password).then((renamed) => {
				this.username = renamed.username;
				try {
					const stored = JSON.parse(storage()?.getItem(storageKey) ?? 'null') as Session | null;
					if (stored) storage()?.setItem(storageKey, JSON.stringify({ ...stored, ...renamed }));
				} catch {
					// the name on screen is right; a stale stored one is corrected by the next sign-in
				}
			}),
		);
	}

	deleteAccount(password: string) {
		return this.change((token) => deleteAccount(token, password).then(() => this.forget()));
	}

	/**
	 * Runs one change to the signed-in account. Answers with the error to show, per form,
	 * rather than through `error` — that one belongs to the sign-in form.
	 */
	private async change<T>(request: (token: string) => Promise<T>) {
		const token = this.token;
		if (!token) return { error: 'Sign in first.' };

		this.busy = true;
		try {
			return { value: await request(token) };
		} catch (error) {
			// a wrong password is a 403; only a 401 means the token itself is gone
			if (error instanceof AudioApiError) this.reject(error.status);
			return { error: error instanceof Error ? error.message : 'Could not reach the audio service.' };
		} finally {
			this.busy = false;
		}
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
	}

	private forget() {
		this.username = null;
		this.token = null;
		storage()?.removeItem(storageKey);
	}
}

export default new Account();
