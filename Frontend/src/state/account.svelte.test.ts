import { beforeEach, expect, it, vi } from 'vitest';
import account from './account.svelte';
import { login, logout, me } from '$requests/accounts';
import { AudioApiError } from '$requests/songs';

vi.mock('$requests/accounts', () => ({
	login: vi.fn(),
	register: vi.fn(),
	logout: vi.fn(),
	me: vi.fn(),
}));

const session = (expiresUtc: string) => ({
	username: 'kris',
	token: 'tok',
	expiresUtc,
});

// jsdom hands out no localStorage under vitest's default document origin, and the
// point of these tests is what the store does with one
const kept = new Map<string, string>();
vi.stubGlobal('localStorage', {
	getItem: (key: string) => kept.get(key) ?? null,
	setItem: (key: string, value: string) => kept.set(key, value),
	removeItem: (key: string) => kept.delete(key),
});

beforeEach(() => {
	kept.clear();
	account.token = null;
	account.username = null;
	vi.mocked(me).mockReset();
	vi.mocked(me).mockRejectedValue(new Error('offline'));
});

/** `load` renews in the background; let that settle before reading what it wrote. */
const settled = () => new Promise(resolve => setTimeout(resolve, 0));

it('keeps the session across a reload', async () => {
	vi.mocked(login).mockResolvedValue(session(new Date(Date.now() + 86_400_000).toISOString()));
	await account.signIn('kris', 'correct horse battery');

	account.token = null;
	account.username = null;
	account.load();

	expect(account.signedIn).toBe(true);
	expect(account.username).toBe('kris');
});

it('drops a token that has already expired', async () => {
	vi.mocked(login).mockResolvedValue(session(new Date(Date.now() - 1000).toISOString()));
	await account.signIn('kris', 'correct horse battery');

	account.load();

	expect(account.signedIn).toBe(false);
	expect(kept.get('musicrain.token')).toBe(undefined);
});

it('takes the slid expiry from Dom over the one it stored', async () => {
	vi.mocked(login).mockResolvedValue(session(new Date(Date.now() + 86_400_000).toISOString()));
	await account.signIn('kris', 'correct horse battery');

	const slid = new Date(Date.now() + 30 * 86_400_000).toISOString();
	vi.mocked(me).mockResolvedValue({ username: 'kris', createdUtc: '', expiresUtc: slid });

	account.load();
	await settled();

	expect(JSON.parse(kept.get('musicrain.token')!).expiresUtc).toBe(slid);
	expect(account.signedIn).toBe(true);
});

it('signs out when the renewal says the token is gone', async () => {
	vi.mocked(login).mockResolvedValue(session(new Date(Date.now() + 86_400_000).toISOString()));
	await account.signIn('kris', 'correct horse battery');

	vi.mocked(me).mockRejectedValue(new AudioApiError('Sign in first.', 401));

	account.load();
	await settled();

	expect(account.signedIn).toBe(false);
	expect(kept.get('musicrain.token')).toBe(undefined);
});

it('keeps the session when the renewal cannot reach Dom', async () => {
	vi.mocked(login).mockResolvedValue(session(new Date(Date.now() + 86_400_000).toISOString()));
	await account.signIn('kris', 'correct horse battery');

	account.load();
	await settled();

	expect(account.signedIn).toBe(true);
	expect(kept.get('musicrain.token')).not.toBe(undefined);
});

it('drops a token Dom refuses, and keeps one over any other failure', async () => {
	vi.mocked(login).mockResolvedValue(session(new Date(Date.now() + 86_400_000).toISOString()));
	await account.signIn('kris', 'correct horse battery');

	account.reject(500);
	expect(account.signedIn).toBe(true);

	account.reject(401);
	expect(account.signedIn).toBe(false);
	expect(kept.get('musicrain.token')).toBe(undefined);
});

it('signs out locally even when Dom cannot be reached', async () => {
	vi.mocked(login).mockResolvedValue(session(new Date(Date.now() + 86_400_000).toISOString()));
	vi.mocked(logout).mockRejectedValue(new Error('offline'));
	await account.signIn('kris', 'correct horse battery');

	await account.signOut();

	expect(account.signedIn).toBe(false);
	expect(kept.get('musicrain.token')).toBe(undefined);
});
