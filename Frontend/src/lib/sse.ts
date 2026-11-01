/**
 * Server-Sent Events out of a `fetch` response body, one `{ event, data }` per frame. `fetch` rather
 * than `EventSource` because `EventSource` cannot send an `Authorization` header, and every staff
 * route wants one.
 *
 * ponytail: no `id:` or `retry:`. Nothing here resumes from an id, and the caller owns reconnecting.
 */
export async function* sse(
	body: ReadableStream<Uint8Array>
): AsyncGenerator<{ event: string; data: string }> {
	const reader = body.pipeThrough(new TextDecoderStream()).getReader();
	let buffer = '';
	let event = '';
	let data: string[] = [];

	for (;;) {
		const { value, done } = await reader.read();
		if (done) return;
		buffer += value;

		// a frame's lines may arrive split across chunks; only whole lines are read
		let end: number;
		// a lone \r at the end may be half of a \r\n still on its way
		while ((end = buffer.search(/\r\n|\r(?!$)|\n/)) >= 0) {
			const line = buffer.slice(0, end);
			buffer = buffer.slice(end + (buffer.startsWith('\r\n', end) ? 2 : 1));

			if (line === '') {
				if (data.length) yield { event: event || 'message', data: data.join('\n') };
				event = '';
				data = [];
			} else if (!line.startsWith(':')) {
				const colon = line.indexOf(':');
				const field = colon < 0 ? line : line.slice(0, colon);
				const content = colon < 0 ? '' : line.slice(colon + 1).replace(/^ /, '');
				if (field === 'event') event = content;
				else if (field === 'data') data.push(content);
			}
		}
	}
}
