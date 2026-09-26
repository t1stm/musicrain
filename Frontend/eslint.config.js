import prettier from 'eslint-config-prettier';
import js from '@eslint/js';
import { includeIgnoreFile } from '@eslint/compat';
import svelte from 'eslint-plugin-svelte';
import globals from 'globals';
import { fileURLToPath } from 'node:url';
import ts from 'typescript-eslint';
import svelteConfig from './svelte.config.js';
const gitignorePath = fileURLToPath(new URL('./.gitignore', import.meta.url));

export default ts.config(
	includeIgnoreFile(gitignorePath),
	{
		ignores: ['.svelte-kit/', 'build/']
	},
	js.configs.recommended,
	...ts.configs.recommended,
	...svelte.configs.recommended,
	prettier,
	...svelte.configs.prettier,
	{
		languageOptions: {
			globals: {
				...globals.browser,
				...globals.node
			}
		}
	},
	{
		files: ['**/*.svelte', '**/*.svelte.ts', '**/*.svelte.js'],
		ignores: ['eslint.config.js', 'svelte.config.js'],

		languageOptions: {
			parserOptions: {
				projectService: true,
				extraFileExtensions: ['.svelte'],
				parser: ts.parser,
				svelteConfig
			}
		}
	},
	{
		files: ['src/components/search/SearchRow.svelte'],
		rules: {
			// Raw source URLs come from the API and may be external, so they cannot use SvelteKit's route resolver.
			'svelte/no-navigation-without-resolve': 'off'
		}
	},
	{
		// resolve() types accept a route, not a query string, so search, artist,
		// room and playlist links resolve the route and append their own query.
		files: [
			'src/routes/(app)/+page.svelte',
			'src/routes/(app)/+layout.svelte',
			'src/routes/(app)/rooms/+page.svelte',
			'src/components/ArtistLink.svelte',
			'src/components/TrackMenu.svelte',
			'src/components/player/layers/track-info/TrackInfo.svelte',
			'src/components/playlist/PlaylistCard.svelte',
			'src/routes/(app)/album/+page.svelte',
			'src/components/queue/Queue.svelte'
		],
		rules: {
			'svelte/no-navigation-without-resolve': 'off'
		}
	}
);
