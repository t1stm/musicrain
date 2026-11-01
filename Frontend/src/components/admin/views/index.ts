import type { Component } from 'svelte';
import type { Snapshot } from '$requests/admin';
import Bot from './Bot.svelte';
import Deezer from './Deezer.svelte';
import Dom from './Dom.svelte';
import Dunav from './Dunav.svelte';
import Fallback from './Fallback.svelte';
import Library from './Library.svelte';
import Selo from './Selo.svelte';
import Spotify from './Spotify.svelte';
import YouTube from './YouTube.svelte';

type View = Component<{ name: string; snapshot: Snapshot }>;

const views: Record<string, View> = {
	dunav: Dunav,
	dom: Dom,
	local: Library,
	bot: Bot,
	selo: Selo,
	deezer: Deezer,
	youtube: YouTube,
	spotify: Spotify
};

/** Oko's lookup: the target's name, or the name without `gaida-`. Anything else shows its JSON. */
export const viewOf = (name: string): View => views[name] ?? views[name.replace(/^gaida-/, '')] ?? Fallback;

const n = (value: unknown) => (typeof value === 'number' ? value.toLocaleString() : '0');
const length = (value: unknown) => (Array.isArray(value) ? value.length : 0);

/** A target's one-line summary, for the overview and the phone's list. Empty for one with nothing to count. */
export function headline(name: string, s: Snapshot | null): string {
	if (!s) return '';
	switch (name.replace(/^gaida-/, '')) {
		case 'dunav':
			return `${n(s.count)} entries`;
		case 'dom':
			return `${length(s.users)} accounts · ${length(s.playlists)} playlists`;
		case 'local':
			return s.scanning ? 'scanning…' : `${n(s.songs)} songs`;
		case 'selo':
			return `${n(s.count)} rooms`;
		case 'bot':
			return `${length(s.accounts)} accounts · ${length(s.players)} playing`;
		case 'deezer':
			return `${n(s.results)} tracks · ${n(s.count)} downloaded`;
		case 'youtube':
			return `${n(s.results)} results · ${n(s.downloads)} downloaded`;
		case 'spotify':
			return `${n(s.results)} remembered`;
		default:
			return '';
	}
}
