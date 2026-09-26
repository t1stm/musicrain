/**
 * Yields each top-level JSON object out of a response body as its closing brace
 * arrives, rather than at the last byte the way `response.json()` does. Anything
 * outside a string that is not part of an object — the array's own brackets,
 * commas, whitespace, newlines — is skipped, so this reads both what ASP.NET Core
 * writes for an `IAsyncEnumerable<T>` (`[{…},{…},…]`, flushed as it goes) and
 * newline-delimited JSON, with no content-type branch.
 *
 * A body that arrives in one piece is not a special case: the last read delivers
 * everything and the whole lot is yielded in one pass. That is what a
 * non-streaming endpoint looks like from here.
 *
 * ponytail: objects only. Every streaming endpoint here returns a list of records,
 * and a general JSON-value parser would be several times the code for no caller.
 */
export async function* streamJson<T>(body: ReadableStream<Uint8Array>): AsyncGenerator<T> {
	// A streaming decode holds back a multi-byte character split across two chunks;
	// the scanner state below does the same for an object split across two reads.
	const reader = body.getReader();
	const decoder = new TextDecoder();
	let buffer = '';
	let cursor = 0;
	let start = -1;
	let depth = 0;
	let inString = false;
	let escaped = false;

	for (;;) {
		const { value, done } = await reader.read();
		// the last read flushes whatever the decoder held back
		buffer += decoder.decode(value, { stream: !done });

		while (cursor < buffer.length) {
			const char = buffer[cursor++];
			if (inString) {
				if (escaped) escaped = false;
				else if (char === '\\') escaped = true;
				else if (char === '"') inString = false;
			} else if (char === '"') inString = true;
			else if (char === '{') {
				if (depth++ === 0) start = cursor - 1;
			} else if (char === '}' && --depth === 0) {
				yield JSON.parse(buffer.slice(start, cursor)) as T;
				// Only the unfinished tail is kept, so a long response does not turn
				// scanning into quadratic string work.
				buffer = buffer.slice(cursor);
				cursor = 0;
				start = -1;
			}
		}

		if (done) return;
	}
}
