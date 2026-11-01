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
	account.role = null;
	account.permissions = [];
	account.checked = false;
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
	vi.mocked(me).mockResolvedValue({ id: 'q3Lx', username: 'kris', createdUtc: '', expiresUtc: slid });

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

const answer = (fields: Record<string, unknown>) => ({
	id: 'q3Lx',
	username: 'kris',
	createdUtc: '',
	expiresUtc: new Date(Date.now() + 30 * 86_400_000).toISOString(),
	...fields
});

it('learns the role from Dom on sign-in, and never stores it', async () => {
	vi.mocked(me).mockResolvedValue(answer({ role: 'admin', permissions: ['caches'] }));
	vi.mocked(login).mockResolvedValue(session(new Date(Date.now() + 86_400_000).toISOString()));

	await account.signIn('kris', 'correct horse battery');
	await settled();

	expect(account.checked).toBe(true);
	expect(account.staff).toBe(true);
	expect(account.owner).toBe(false);
	expect(account.can('caches')).toBe(true);
	expect(account.can('rooms')).toBe(false);
	expect(kept.get('musicrain.token')).not.toContain('admin');
});

it('reads an answer with no role fields as no role', async () => {
	vi.mocked(me).mockResolvedValue(answer({}));
	vi.mocked(login).mockResolvedValue(session(new Date(Date.now() + 86_400_000).toISOString()));

	await account.signIn('kris', 'correct horse battery');
	await settled();

	expect(account.checked).toBe(true);
	expect(account.staff).toBe(false);
	expect(account.permissions).toEqual([]);
});

it('stays unchecked while Dom cannot be reached, so staff pages wait rather than refuse', async () => {
	vi.mocked(login).mockResolvedValue(session(new Date(Date.now() + 86_400_000).toISOString()));

	await account.signIn('kris', 'correct horse battery');
	await settled();

	expect(account.checked).toBe(false);
});

it('forgets the role on sign-out', async () => {
	vi.mocked(me).mockResolvedValue(answer({ role: 'owner', permissions: ['accounts', 'studio'] }));
	vi.mocked(login).mockResolvedValue(session(new Date(Date.now() + 86_400_000).toISOString()));
	vi.mocked(logout).mockResolvedValue(null);
	await account.signIn('kris', 'correct horse battery');
	await settled();
	expect(account.owner).toBe(true);

	await account.signOut();

	expect([account.role, account.permissions, account.checked]).toEqual([null, [], false]);
});

it('picks up a role that changed, on refresh', async () => {
	vi.mocked(me).mockResolvedValue(answer({}));
	vi.mocked(login).mockResolvedValue(session(new Date(Date.now() + 86_400_000).toISOString()));
	await account.signIn('kris', 'correct horse battery');
	await settled();

	vi.mocked(me).mockResolvedValue(answer({ role: 'owner', permissions: ['accounts'] }));
	await account.refresh();

	expect(account.owner).toBe(true);
});
