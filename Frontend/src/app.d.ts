// See https://svelte.dev/docs/kit/types#app.d.ts
// for information about these interfaces
declare global {
	namespace App {
		// interface Error {}
		// interface Locals {}
		// interface PageData {}
		interface PageState {
			/** Entries this page has pushed without leaving itself — see `$lib/backWatcher.svelte.ts`. */
			depth?: number;
			/** Which roll the home page is showing — see `$lib/rollHistory`. */
			home?: number;
		}
		// interface Platform {}
	}

	/**
	 * The Launch Handler API: with `focus-existing` in the manifest, a link the installed app
	 * captures is handed to the window already open instead of loading a new one. Not in
	 * TypeScript's DOM types yet, and not in Safari or Firefox at all.
	 */
	interface Window {
		launchQueue?: { setConsumer(consumer: (params: { targetURL?: string }) => void): void };
	}
}

export {};
