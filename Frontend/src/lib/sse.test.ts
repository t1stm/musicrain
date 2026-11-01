import { expect, it } from 'vitest';
import { sse } from './sse';

const body = (...chunks: string[]) =>
	new ReadableStream<Uint8Array>({
		start(controller) {
			for (const chunk of chunks) controller.enqueue(new TextEncoder().encode(chunk));
			controller.close();
		}
	});

const all = async (stream: ReadableStream<Uint8Array>) => {
	const frames = [];
	for await (const frame of sse(stream)) frames.push(frame);
	return frames;
};

it('reads frames split across chunks', async () => {
	expect(
		await all(body('event: activ', 'ity\ndata: {"a"', ':1}\n', '\nevent: activity\ndata: 2\n\n'))
	).toEqual([
		{ event: 'activity', data: '{"a":1}' },
		{ event: 'activity', data: '2' }
	]);
});

it('joins multi-line data, skips comments, and names unnamed events message', async () => {
	expect(await all(body(': keepalive\r\n\r\ndata: one\r\ndata:two\r\n\r\n'))).toEqual([
		{ event: 'message', data: 'one\ntwo' }
	]);
});

it('drops a frame the stream ended in the middle of', async () => {
	expect(await all(body('data: whole\n\ndata: half'))).toEqual([{ event: 'message', data: 'whole' }]);
});
