import { describe, expect, it, vi } from 'vitest';
import { getLyrics } from './lyrics';
import { AudioApiError } from './songs';

const body = {
	type: 'Synchronized',
	source: 'LRCLIB',
	lines: [{ at: 12.34, text: 'Alle warten' }],
	text: 'Alle warten',
	matched: { title: 'Sonne', artist: 'Rammstein', length: 272 }
};

function respond(status: number, payload?: unknown) {
	const json = vi.fn(async () => payload);
	const urls: string[] = [];
	const fetcher = async (url: RequestInfo | URL) => {
		urls.push(String(url));
		return { ok: status < 400, status, json } as unknown as Response;
	};

	return { fetcher, json, urls };
}

describe('getLyrics', () => {
	it('parses a 200', async () => {
		const { fetcher } = respond(200, body);
		await expect(getLyrics(fetcher, 'audio://x')).resolves.toMatchObject({ type: 'Synchronized' });
	});

	it('answers null for a 204 without parsing a body', async () => {
		const { fetcher, json } = respond(204);

		await expect(getLyrics(fetcher, 'audio://x')).resolves.toBeNull();
		expect(json).not.toHaveBeenCalled();
	});

	it('percent-encodes the id into the query rather than into the path', async () => {
		const { fetcher, urls } = respond(204);
		await getLyrics(fetcher, 'audio://ramsonne-x9');

		expect(urls[0]).toContain('id=audio%3A%2F%2Framsonne-x9');
		expect(urls[0]).not.toContain('id=audio://');
	});

	it('throws AudioApiError on anything else, with the service message', async () => {
		const { fetcher } = respond(400, { error: { code: 'invalid_query', message: 'A track id is required.' } });

		await expect(getLyrics(fetcher, '')).rejects.toThrow(AudioApiError);
	});

	it('passes the abort signal through, so an abort rejects rather than resolving', async () => {
		const controller = new AbortController();
		const fetcher = vi.fn(async (_url: RequestInfo | URL, init?: RequestInit) => {
			if (init?.signal?.aborted) throw new DOMException('aborted', 'AbortError');
			return { ok: true, status: 200, json: async () => body } as unknown as Response;
		});

		controller.abort();
		await expect(getLyrics(fetcher, 'audio://x', controller.signal)).rejects.toThrow('aborted');
	});
});
