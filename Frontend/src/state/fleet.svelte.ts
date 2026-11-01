import { sse } from '$lib/sse';
import { act, events, snapshot, type FleetSnapshot, type Params, type ServiceStatus } from '$requests/admin';
import { AudioApiError } from '$requests/songs';
import account from '$states/account.svelte';

const POLL_MS = 2000;
const FEED_CAP = 500;

/** One request a watched service reported, as the feed shows it. */
export type FeedLine = {
	id: number;
	target: string;
	status: number;
	method: string;
	path: string;
	ms: number;
};

/**
 * Every target Oko watches, polled every 2 s while an /admin page is open and the tab is visible,
 * and the merged live feed beside it. Nothing runs while no /admin page is mounted: the layout
 * starts it and stops it, the same rule Oko applies to the services.
 */
class Fleet {
	latest: FleetSnapshot | null = $state(null);
	error = $state('');
	feed: FeedLine[] = $state([]);
	/** The feed is connected. False while it is reconnecting. */
	following = $state(false);
	/** Counts actions taken from here, so a table on screen knows to load again. */
	actions = $state(0);

	private users = 0;
	private timer: ReturnType<typeof setInterval> | undefined;
	private stream: AbortController | null = null;
	private nextId = 0;

	/** The target whose snapshot says it is the library pod, keyed on what it reports, not on config. */
	get libraryTarget() {
		return this.latest?.services.find((service) => service.snapshot?.service === 'gaida-local')?.name ?? null;
	}

	service(name: string): ServiceStatus | undefined {
		return this.latest?.services.find((service) => service.name === name);
	}

	/** Whether the signed-in account may act on a target: its permission, or the owner for none. */
	may(name: string) {
		const permission = this.service(name)?.permission ?? null;
		return permission ? account.can(permission) : account.owner;
	}

	/** Why not, for a disabled button's title. */
	needs(name: string) {
		const permission = this.service(name)?.permission ?? null;
		return permission ? `Needs the ${permission} permission` : `Only the owner can act on ${name}`;
	}

	/** Start watching; the returned function stops. Counted, so nested pages share one poll. */
	start(): () => void {
		if (this.users++ === 0) {
			this.poll();
			this.timer = setInterval(() => this.poll(), POLL_MS);
			document.addEventListener('visibilitychange', this.visibility);
			this.follow();
		}
		return () => {
			if (--this.users > 0) return;
			clearInterval(this.timer);
			document.removeEventListener('visibilitychange', this.visibility);
			this.unfollow();
		};
	}

	async refresh() {
		try {
			this.latest = await snapshot();
			this.error = '';
		} catch (error) {
			this.error = error instanceof Error ? error.message : 'Could not reach the staff gateway.';
			// demoted since /Me last answered: ask again, and the gate closes on its own
			if (error instanceof AudioApiError && error.status === 403) account.refresh();
		}
	}

	/** Runs one action, then refreshes at once rather than at the next poll, as Oko's page did. */
	async act<T = unknown>(target: string, action: string, params?: Params) {
		try {
			return await act<T>(target, action, params);
		} finally {
			this.actions++;
			await this.refresh();
		}
	}

	private poll() {
		if (document.visibilityState === 'visible') this.refresh();
	}

	// no feed while the tab is in the background: Oko then holds no upstream connections either
	private visibility = () => {
		if (document.visibilityState === 'visible') {
			this.follow();
			this.poll();
		} else this.unfollow();
	};

	/** Keeps the feed open, 5 s between attempts, until `unfollow`. */
	private async follow() {
		if (this.stream) return;
		const stream = (this.stream = new AbortController());

		while (!stream.signal.aborted) {
			try {
				const body = await events(stream.signal);
				this.following = true;
				for await (const frame of sse(body)) {
					if (frame.event !== 'activity') continue;
					const item = JSON.parse(frame.data);
					const request = item.data ?? {};
					this.feed.unshift({
						id: this.nextId++,
						target: item.target,
						status: request.status,
						method: request.method,
						path: request.path,
						ms: request.ms
					});
					if (this.feed.length > FEED_CAP) this.feed.length = FEED_CAP;
				}
			} catch {
				// dropped, refused or never reached: the same wait either way
			}
			this.following = false;
			if (stream.signal.aborted) return;
			await new Promise((wake) => setTimeout(wake, 5000));
		}
	}

	private unfollow() {
		this.stream?.abort();
		this.stream = null;
		this.following = false;
	}
}

const fleet = new Fleet();
export default fleet;
