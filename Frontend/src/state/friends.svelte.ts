import {
	acceptInvite,
	currentInvite,
	endInvite,
	getFriends,
	openInvite,
	unfriend,
	type Invite,
} from '$requests/accounts';
import { AudioApiError } from '$requests/songs';
import { PUBLIC_SITE_URL } from '$env/static/public';
import { isDiscordActivity } from '$lib/discord';
import account from './account.svelte';

/** A code as it is shown and read out: two groups of four. */
export const spaced = (code: string) => `${code.slice(0, 4)}-${code.slice(4)}`;

/**
 * The link a code travels as. It has to be on the origin the app is installed from, or an
 * installed app will not take it — and inside the Discord activity `location` is Discord's.
 */
export function inviteLink(code: string) {
	const origin = isDiscordActivity ? PUBLIC_SITE_URL : location.origin;
	return `${origin}/settings/friends?code=${code}`;
}

/** Seconds a code has left at `now`, never below zero. */
export const secondsLeft = (expiresUtc: string, now: number) =>
	Math.max(0, (Date.parse(expiresUtc) - now) / 1000);

/** `9:05` — a countdown as the eyebrow and the button say it. */
export const minutesAndSeconds = (seconds: number) =>
	`${Math.floor(seconds / 60)}:${String(Math.floor(seconds % 60)).padStart(2, '0')}`;

/**
 * Your friends, and the code that makes more of them. The code is for a group:
 * everybody who uses it in its fifteen minutes becomes a friend, so the screen
 * showing it keeps it up and lists who has joined.
 */
class Friends {
	list: string[] = $state([]);
	/** The token `list` was read with: a sign-out, or another account, makes it unread again. */
	private loadedFor = $state<string | null>(null);
	/** Whether `list` is this account's, rather than the empty list before the first read. */
	get loaded() {
		return !!account.token && this.loadedFor === account.token;
	}
	/** The live code as last read, or `null` when there is none. */
	invite: Invite | null = $state(null);
	/** The server says the code is gone before its time: ended, from here or another device. */
	ended = $state(false);
	error = $state('');

	async load() {
		if (!account.token) {
			this.list = [];
			return;
		}

		await this.attempt(async (token) => {
			this.list = await getFriends(token);
			this.loadedFor = token;
		});
	}

	/** Shows the live code, or makes one. */
	async openInvite() {
		await this.attempt(async (token) => {
			this.invite = await openInvite(token);
			this.ended = false;
		});

		return this.invite;
	}

	/**
	 * Who has joined, without ever making a code. New names reload the list, so the
	 * page behind the gate already has them when it closes.
	 */
	async pollInvite() {
		const token = account.token;
		if (!token) return;

		try {
			const next = await currentInvite(token);
			const grew = next.joined.length > (this.invite?.joined.length ?? 0);
			this.invite = next;
			this.ended = false;
			if (grew) await this.load();
		} catch (error) {
			if (!(error instanceof AudioApiError)) return;
			account.reject(error.status);
			// ran out, or ended somewhere else; which of the two is the clock's to say
			if (error.status === 404) this.ended = true;
		}
	}

	async endInvite() {
		await this.attempt((token) => endInvite(token));
		this.ended = true;
	}

	/** `null` when it did not work; the reason is in `error`. */
	async accept(code: string) {
		let answer: { username: string; alreadyFriends: boolean } | null = null;
		await this.attempt(async (token) => {
			answer = await acceptInvite(token, code);
			await this.load();
		});

		return answer as { username: string; alreadyFriends: boolean } | null;
	}

	async remove(username: string) {
		await this.attempt(async (token) => {
			await unfriend(token, username);
			this.list = this.list.filter((name) => name !== username);
		});
	}

	private async attempt(work: (token: string) => Promise<unknown>) {
		if (!account.token) return;

		this.error = '';
		try {
			await work(account.token);
		} catch (error) {
			if (error instanceof AudioApiError) account.reject(error.status);
			this.error = error instanceof Error ? error.message : 'Could not reach the audio service.';
		}
	}
}

export default new Friends();
