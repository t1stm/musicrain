import { DiscordSDK } from '@discord/embedded-app-sdk';
import { PUBLIC_API_URL } from '$env/static/public';

/**
 * Discord serves an activity from `<client_id>.discordsays.com` and appends the
 * `frame_id` / `instance_id` query params the SDK needs. Either signal is enough.
 */
export const isDiscordActivity =
	typeof location !== 'undefined' &&
	(location.hostname.endsWith('.discordsays.com') || new URLSearchParams(location.search).has('frame_id'));

/**
 * `PUBLIC_API_URL` comes from `.env`, overridable in `.env.local`, and is inlined
 * at build time — so a deployment picks its API without touching this file. The
 * committed value is a placeholder: a real deployment sets its own.
 *
 * Inside the activity iframe it is ignored: every external host is blocked by
 * Discord's CSP, and only the URL mappings configured in the Developer Portal are
 * reachable, under `/.proxy/<prefix>`. See the README's Discord Activity section.
 */
export const audioApi = isDiscordActivity ? '/.proxy/api/Audio' : PUBLIC_API_URL;

/**
 * Socket URL for an API path. Browser-only: `audioApi` may be a proxy path, so
 * the origin has to come from `location`. `https` becomes `wss`, `http` `ws`.
 * Discord passes websocket upgrades through the same `/api` mapping, so the
 * activity needs no extra Developer Portal entry.
 */
export function audioWsUrl(path: string) {
	return new URL(audioApi + path, location.origin).href.replace(/^http/, 'ws');
}

export let sdk: DiscordSDK | null = null;

/** Name and avatar of whoever launched the activity, once `initDiscord` ran. */
export let discordUser: { name: string; avatarUrl: string | null } | null = null;

/**
 * Handshakes with the Discord client. Without `ready()` the activity sits on
 * Discord's loading screen forever. Outside Discord this is a no-op.
 *
 * After this resolves, `sdk.channelId` / `sdk.guildId` / `sdk.instanceId`
 * identify the room the activity was launched in.
 */
export async function initDiscord() {
	if (!isDiscordActivity || sdk) return;

	const clientId = import.meta.env.VITE_DISCORD_CLIENT_ID;
	if (!clientId) {
		console.error('VITE_DISCORD_CLIENT_ID is unset — the activity cannot start.');
		return;
	}

	sdk = new DiscordSDK(clientId);
	// READY carries the basic user object — no scopes, no OAuth code exchange, so
	// no token endpoint on the API. `subscribe` registers READY listeners on the
	// bus synchronously (it skips the RPC round trip for this one event), so this
	// is in place before the handshake reply lands.
	void sdk.subscribe('READY', ({ user }) => {
		if (!user) return;
		discordUser = {
			name: user.username,
			// ponytail: no avatar means no URL — the header falls back to its icon.
			avatarUrl: user.avatar ? `https://cdn.discordapp.com/avatars/${user.id}/${user.avatar}.png?size=64` : null
		};
	});
	await sdk.ready();
}

/**
 * Readable without authenticating, which is what the room mapping runs on.
 * A real channel name needs `sdk.commands.authenticate()`, and that needs a
 * server-side OAuth code exchange the API does not have yet. The launching
 * user's own name and avatar do not — see `discordUser`.
 */
export function discordIds() {
	if (!sdk) return null;
	return {
		instanceId: sdk.instanceId,
		channelId: sdk.channelId,
		guildId: sdk.guildId
	};
}
