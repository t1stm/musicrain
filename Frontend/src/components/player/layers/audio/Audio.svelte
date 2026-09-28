<script lang="ts">
	import current from '$states/current.svelte';
	import audio from '$states/audio.svelte';
	import queue from '$states/queue.svelte';
	import session from '$states/session.svelte';
	import quality from '$states/quality.svelte';
	import skipped from '$states/skipped.svelte';
	import { dropPrefetch, prefetchSong } from '$requests/songs';
	import { interpolate } from '$lib/playbackClock';
	let url = $derived(current.url);
	let element: HTMLAudioElement | undefined = $state();

	let context: AudioContext | undefined;
	// The element's clock is the inaccurate part: `timeupdate` reports it about
	// four times a second and some browsers quantise it on top of that. The
	// context's clock is the one the hardware actually plays against, so the
	// position is carried on that between the element's own reports, and every
	// report re-anchors the pair so the two can never drift apart.
	let anchorMedia = 0;
	let anchorClock = 0;
	// interpolating is only honest while the element is really moving; a stall
	// stops `timeupdate` but not the context clock, and the position would run
	// away from the sound.
	let advancing = false;

	// ponytail: `createMediaElementSource` may be called once per element for the
	// life of the page, and the element outlives every track — so the graph is
	// built once and never rebuilt. No GainNode: `element.volume` still applies
	// upstream of the node. Add one if a browser turns out to ignore it.
	$effect(() => {
		if (!element || context) return;
		const built = new AudioContext({ latencyHint: 'playback' });
		built.createMediaElementSource(element).connect(built.destination);
		context = built;
		// Suspended at construction is the autoplay policy answering: this device
		// makes no sound at all until a gesture resumes the graph. Everything
		// routed through it is silence until then — see `audio.blocked`.
		audio.blocked = built.state === 'suspended';
		audio.unblock = async () => {
			await built.resume();
			audio.blocked = built.state !== 'running';
		};
		return () => {
			context = undefined;
			void built.close();
		};
	});

	// The whole distance between the element's clock and the ear: `baseLatency` is
	// what the graph itself holds, `outputLatency` is destination to device, and
	// the knob is the rest — a Bluetooth link, or a Safari that reports no
	// `outputLatency` at all. Everything the element reports is this far ahead of
	// the sound, everything the room says is a position in the sound, and every
	// crossing between the two domains pays it.
	const latency = () => {
		const measured = context ? context.baseLatency + (context.outputLatency || 0) : 0;
		// Mirrored out for the strip, which shows the whole latency rather than the
		// knob alone. Guarded because this runs per position read: the number is a
		// property of the output path and only moves when the device does.
		const rounded = Math.round(measured * 1000);
		if (rounded !== audio.measuredMs) audio.measuredMs = rounded;
		return measured + audio.latencyMs / 1000;
	};

	function anchor() {
		if (!element) return;
		anchorMedia = element.currentTime;
		anchorClock = context ? context.currentTime : 0;
		// writing here too is what keeps a background tab moving: the sampling timer
		// is throttled to about once a second there, `timeupdate` is not. Same
		// conversion `position` makes, or the reported position steps by the
		// latency four times a second and the room samples the step.
		audio.currentSeconds = interpolate(anchorMedia, 0, 1, latency());
	}

	function hold() {
		advancing = false;
		anchor();
	}

	function resume() {
		advancing = true;
		anchor();
	}

	// what you hear trails the clock by whatever the graph has not played out
	// yet; reporting the audible position is the point of doing this at all,
	// since a room syncs against sound, not buffers.
	function position() {
		if (!context || !advancing) return audio.currentSeconds;
		return interpolate(
			anchorMedia,
			context.currentTime - anchorClock,
			element?.playbackRate ?? 1,
			latency()
		);
	}

	// The room reads this per `sync` reply rather than the sampled state, so the
	// polling rate below costs accuracy nothing. A closed context falls the
	// function back to the sampled value on its own, so unmounting needs no undo.
	audio.positionNow = position;

	// ponytail: 10 Hz, not a frame loop. This feeds the display only — a seek bar
	// a few hundred pixels wide and a readout in whole seconds — and every write
	// costs a layout, a paint, a re-raster of the player's backdrop blur and a
	// mediaSession IPC. Raise it if a bar ever gets wide enough to show the steps.
	$effect(() => {
		if (!element || audio.paused) return;
		const timer = setInterval(() => (audio.currentSeconds = position()), 100);
		return () => clearInterval(timer);
	});

	// ponytail: no timer of its own — this rides the 10 Hz sampling above, and
	// `preloadSong` dedupes, so re-running through the whole window costs a Set
	// lookup. 20 s is the head start; widen it if a cold encode ever outlasts it.
	$effect(() => {
		if (current.lengthSeconds < 1) return;
		if (current.lengthSeconds - audio.currentSeconds < 20) queue.preloadNext();
	});

	// The next track's body, pulled down while this one plays, so the switch is a
	// local resource swap instead of a request — see `prefetchSong`.
	//
	// ponytail: no timer, no trigger of its own. It rides `bufferedSeconds`, which
	// `oncanplaythrough` pins to the track length: that is the moment this track
	// owes the network nothing, so the download competes with no playback. It runs
	// again on its own whenever the next id or the quality it would be encoded at
	// changes, and `prefetchSong` makes the unchanged case a key comparison.
	$effect(() => {
		const next = queue.items[queue.currentIndex + 1]?.id;
		void quality.codec;
		void quality.bitrate;

		if (!next) return dropPrefetch();
		if (current.lengthSeconds < 1 || audio.bufferedSeconds < current.lengthSeconds) return;

		prefetchSong(next);
	});

	// The room's clock steers the rate to hold everyone together. `preservesPitch`
	// off is deliberate: on, the browser time-stretches, and a phase vocoder is
	// audibly grainy on transients. Off, it resamples — and the loop settles
	// within a few parts in ten thousand of 1.0, which is a pitch shift of about
	// a cent. The clamp that keeps it there lives in `syncClock.ts`.
	$effect(() => {
		if (!element) return;
		element.preservesPitch = false;
		element.playbackRate = audio.rate;
	});

	// a write to the state the element did not make is a seek. The tolerance is
	// what stops this component's own writes from bouncing back in as one — which
	// needs the target back in the element's domain first, or a client lands the
	// latency late on every correction and this component's own reports read as
	// seeks once the graph buffers deeper than the tolerance.
	$effect(() => {
		const seconds = audio.currentSeconds;
		if (!element) return;
		const target = seconds + latency();
		if (Math.abs(element.currentTime - target) > 0.25) element.currentTime = target;
	});

	// ponytail: not `bind:paused`. Its write runs in the same flush as a `src`
	// change, the load algorithm aborts the play(), and its catch writes
	// `paused = true` back — so the room says playing and this client sits
	// silent. `audio.paused` is the intent here; `oncanplay` re-applies it once
	// the element can honour it. Only a real autoplay block flips the intent.
	function apply() {
		if (!element) return;
		if (audio.paused) return element.pause();

		// routed through the graph, a suspended context is silence no matter what
		// the element does — and it starts suspended until a gesture resumes it.
		void context?.resume();
		element.play().catch((error: DOMException) => {
			if (error.name === 'NotAllowedError') audio.paused = true;
		});
	}

	// a src change needs no dependency here: the element pauses itself on load and
	// `oncanplay` re-applies the intent to the new resource.
	$effect(apply);

	// A connection dropped mid-download (NS_BINDING_ERROR, a request that timed
	// out) lands here as MEDIA_ERR_NETWORK, and the element then gives up for
	// good: nothing re-requests a resource it has already failed. So re-request
	// it, from where the sound stopped, and back off so a link that is genuinely
	// dead stops trying instead of hammering.
	let retries = 0;
	let retryTimer: ReturnType<typeof setTimeout> | undefined;

	$effect(() => {
		void url; // every track gets its own budget
		retries = 0;
		return () => clearTimeout(retryTimer);
	});

	function recover() {
		// Nothing is loaded: the element fails `src=""` on every idle join, and an
		// empty room has already had its barrier answered by the session.
		if (!url) return;

		// A truncated download surfaces as MEDIA_ERR_NETWORK, or as MEDIA_ERR_DECODE
		// when the bytes ran out mid-frame — so both are worth another go. So is
		// SRC_NOT_SUPPORTED: Chrome answers a 502 or 503 from the encoder with it
		// ("Format error"), the same as a codec it cannot play, and a busy encoder
		// is usually free a moment later. A codec that really is refused costs the
		// retry budget, a few seconds, before this client sits the track out. Only
		// ABORTED is never retried, since that is us.
		const failure = element?.error;
		const code = failure?.code;
		// Chrome files bytes it cannot parse under SRC_NOT_SUPPORTED too, but says
		// so: "PipelineStatus::DEMUXER_ERROR_COULD_NOT_OPEN". That body arrived and
		// is broken, and it came with a year-long cache header, so a retry is
		// answered with the same bytes. Only a status reaches here without a body.
		const unreadable = failure?.message.includes('DEMUXER_ERROR') ?? false;
		const retryable = code !== undefined && code !== MediaError.MEDIA_ERR_ABORTED && !unreadable;
		const retrying = !!element && retryable && retries < 4;

		// `message` is where the useful half lives — it is what carries Firefox's
		// NS_BINDING_ERROR and the rest of the platform's own wording; the code
		// alone only says which of four buckets it fell into.
		console.error(`audio ${retrying ? 'download failed, retrying' : 'gave up'}: ${current.name}`, {
			code,
			kind: ['', 'ABORTED', 'NETWORK', 'DECODE', 'SRC_NOT_SUPPORTED'][code ?? 0],
			message: failure?.message,
			src: element?.currentSrc || url,
			networkState: element?.networkState,
			readyState: element?.readyState,
			seconds: element?.currentTime,
			buffered: audio.bufferedSeconds,
			of: current.lengthSeconds,
			attempt: retries + 1
		});

		if (!retrying || !element) {
			// The element keeps the HTTP status to itself: a 502 or 503 lands here as
			// NETWORK or SRC_NOT_SUPPORTED depending on the browser.
			session.giveUp(
				unreadable ? 'decode' : ['network', 'network', 'network', 'decode', 'unavailable'][code ?? 0]
			);
			return;
		}

		// The room is held for one quick retry, which is all a busy encoder needs.
		// Failing again is more likely a file that will not load, and waiting out
		// the rest of the budget costs everyone seconds at the top of the track.
		// Answered now, the room starts, and a retry that lands joins it where it
		// is, the way anyone who joins late does.
		if (retries >= 1) session.reportLoaded();

		const resumeAt = element.currentTime;
		// ponytail: `load()` re-requests the whole resource and the HTTP cache is
		// what makes the part already downloaded cheap. Swap in an explicit Range
		// request if the server ever serves this uncacheable.
		retryTimer = setTimeout(() => {
			if (!element) return;
			element.load();
			// before metadata there is nothing to seek: this sets the default playback
			// start position instead, and the element lands there once it loads.
			element.currentTime = resumeAt;
		}, 250 * 2 ** retries++);
	}
</script>

<audio
	bind:this={element}
	src={url}
	preload="auto"
	crossorigin="anonymous"
	bind:volume={audio.volume}
	onplaying={resume}
	ontimeupdate={anchor}
	onseeked={anchor}
	onpause={hold}
	onwaiting={hold}
	onstalled={hold}
	onemptied={hold}
	oncanplay={apply}
	onloadstart={() => {
		// bufferedSeconds only ever climbs, so it has to go back to zero with the
		// resource it describes — otherwise the next track inherits this one's
		// buffer and the gauge never fills again.
		audio.bufferedSeconds = 0;
	}}
	onprogress={(event) => {
		const player = event.currentTarget;
		const buffer = player.buffered;
		if (buffer.length < 1) return;

		const end = buffer.end(buffer.length - 1);
		if (end < audio.bufferedSeconds) return;
		audio.bufferedSeconds = end;
	}}
	oncanplaythrough={() => {
		audio.bufferedSeconds = current.lengthSeconds;
		session.reportLoaded();
		skipped.loaded();
		// A retry that landed after the room started is a late join, and the
		// clock spent its opening snap while the element had nothing. Without a
		// fresh one, half a second behind is steered away at 2% over half a minute.
		if (retries) session.resync();
	}}
	onerror={recover}
	onended={() => {
		hold();
		// in a room the server owns the advance: report once and wait for the
		// finishing barrier to release for everybody
		if (session.inRoom) session.reportEnded();
		else queue.nextTrack();
	}}
>
</audio>
