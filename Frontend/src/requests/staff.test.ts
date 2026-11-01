import { beforeEach, expect, it, vi } from 'vitest';
import account from '$states/account.svelte';
import { AudioApiError } from './songs';
import { staffCall } from './staff';

vi.mock('$states/account.svelte', () => ({
	default: { token: 'tok' as string | null, reject: vi.fn() }
}));

const fetcher = vi.fn();
vi.stubGlobal('fetch', fetcher);

const answer = (status: number, body?: unknown) =>
	new Response(body === undefined ? null : JSON.stringify(body), { status });

beforeEach(() => {
	account.token = 'tok';
});

it('sends the bearer token, and a plain object as JSON', async () => {
	fetcher.mockResolvedValue(answer(200, { role: 'admin' }));

	expect(await staffCall('/Staff/ana', { method: 'PUT', body: { permissions: ['caches'] } })).toEqual({
		role: 'admin'
	});

	const [url, init] = fetcher.mock.calls[0];
	const headers = new Headers(init.headers);
	expect(url).toBe('/Staff/ana');
	expect(headers.get('Authorization')).toBe('Bearer tok');
	expect(headers.get('Content-Type')).toBe('application/json');
	expect(init.body).toBe('{"permissions":["caches"]}');
});

it('reads a 204 as nothing', async () => {
	fetcher.mockResolvedValue(answer(204));
	expect(await staffCall('/Staff/ana', { method: 'DELETE' })).toBe(null);
});

it('surfaces the message from either error envelope', async () => {
	fetcher.mockResolvedValueOnce(
		answer(403, { error: { code: 'forbidden', message: 'Only the owner manages staff.' } })
	);
	await expect(staffCall('/Staff')).rejects.toEqual(new AudioApiError('Only the owner manages staff.', 403));

	fetcher.mockResolvedValueOnce(answer(400, { error: 'No such song.' }));
	await expect(staffCall('/Lirika/x')).rejects.toThrow('No such song.');

	fetcher.mockResolvedValueOnce(new Response('<html>', { status: 502 }));
	await expect(staffCall('/Admin/targets')).rejects.toThrow('The audio service returned 502.');
});

it('hands every failure to the account, which signs out on a 401', async () => {
	fetcher.mockResolvedValue(answer(401, { error: { code: 'unauthorized', message: 'Sign in first.' } }));

	await expect(staffCall('/Staff')).rejects.toMatchObject({ status: 401 });
	expect(account.reject).toHaveBeenCalledWith(401);
});

it('never calls fetch without a token', async () => {
	account.token = null;

	await expect(staffCall('/Staff')).rejects.toMatchObject({ status: 401 });
	expect(fetcher).not.toHaveBeenCalled();
});
