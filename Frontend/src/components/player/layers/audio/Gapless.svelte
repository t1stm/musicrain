<script lang="ts">
	import audio from '$states/audio.svelte';
	import current from '$states/current.svelte';
	import queue from '$states/queue.svelte';
	import quality from '$states/quality.svelte';
	import { downloadUrl } from '$requests/songs';
	import { interpolate } from '$lib/playbackClock';
	import { Splicer, TAIL } from '$lib/splicer';

	// The engine outside a room. Every track is decoded to PCM, so the next one can
	// be handed the exact context time the current buffer runs out: the join is
	// made in the audio thread and no timer can be late for it. An <audio> element
	// cannot do that at all — swapping its `src` is a load, and a load is the gap
	// this exists to close. `Splicer` holds the scheduling, `stream` the decoding.
	//
	// A room stays on `Audio.svelte`: the sync clock needs the element's streaming
	// start and its own steerable rate, and a room already agrees on where the
	// join is. See the fork in `Player.svelte`.

	let context: AudioContext | undefined;
	let gain: GainNode | undefined;
	let splicer: Splicer | undefined;
	let keeper: HTMLAudioElement | undefined = $state();
	let keeperSrc = $state('');

	/** Decoded PCM, by codec/bitrate/id. Never more than the track playing and the
	 *  one after it: decoded audio is about 10 MB a minute whatever it arrived as. */
	const decoded: Record<string, AudioBuffer> = {};
	/** Downloads in flight, so two triggers for one track cost one request. */
	const loading: Record<string, Promise<AudioBuffer | null>> = {};

	/** Whether this browser decodes a truncated stream. Chrome and Safari do; one
	 *  refusal anywhere else turns it off for good and every track waits for its
	 *  whole body, since the answer is a property of the browser, not the track. */
	let prefixes = true;

	/** The position the current track is meant to be at, until a decode reaches it:
	 *  a position carried over from the other engine, or a seek past what has been
	 *  decoded so far. Zero once it has been honoured. */
	let pending = 0;

	/** The track this component is working towards, which is the current track
	 *  from the moment it is chosen rather than from the moment it is decoded — a
	 *  download that lands for a track nobody is waiting for any more is dropped
	 *  against this. */
	let wanted = '';

	$effect(() => {
		const built = new AudioContext({ latencyHint: 'playback' });
		const volume = built.createGain();
		volume.connect(built.destination);

		const engine = new Splicer(built, volume);
		engine.onadvance = advance;
		engine.onend = () => queue.nextTrack();

		context = built;
		gain = volume;
		splicer = engine;
		keeperSrc = silence();

		// Suspended at construction is the autoplay policy answering: this device
		// makes no sound until a gesture resumes the graph — see `audio.blocked`.
		audio.blocked = built.state === 'suspended';
		audio.unblock = async () => {
			await built.resume();
			audio.blocked = built.state !== 'running';
		};

		return () => {
			engine.dispose();
			URL.revokeObjectURL(keeperSrc);
			context = undefined;
			gain = undefined;
			splicer = undefined;
			for (const stale of Object.keys(decoded)) delete decoded[stale];
			for (const stale of Object.keys(loading)) delete loading[stale];
			void built.close();
		};
	});

	// The whole distance between the clock and the ear: `baseLatency` is what the
	// graph holds, `outputLatency` is destination to device, and the knob is the
	// rest — a Bluetooth link, or a Safari that reports no `outputLatency` at all.
	// Same conversion as the element engine makes, for the same reason: everything
	// the graph reports is this far ahead of the sound.
	function latency() {
		const measured = context ? context.baseLatency + (context.outputLatency || 0) : 0;
		// Mirrored out for the session strip, which shows the whole latency rather
		// than the knob alone. Guarded because this runs per position read.
		const rounded = Math.round(measured * 1000);
		if (rounded !== audio.measuredMs) audio.measuredMs = rounded;
		return measured + audio.latencyMs / 1000;
	}

	function position() {
		if (!splicer) return audio.currentSeconds;
		return interpolate(splicer.position(), 0, 1, latency());
	}

	audio.positionNow = position;

	function key(id: string) {
		return `${quality.codec}/${quality.bitrate}/${id}`;
	}

	/**
	 * Web Audio plays a decoded buffer, not a stream, so a track used to wait on
	 * its whole body before a note came out — tens of megabytes of a cold FLAC
	 * before anything was audible.
	 *
	 * It does not have to be the whole body. Everything the API serves is a
	 * streaming container (Ogg, ADTS, MP3, native FLAC — no MP4 anywhere), so any
	 * prefix of one is a valid short stream, and decoding a prefix gives back the
	 * PCM the whole file gives back, sample for sample, bar the handful at the cut
	 * that `TAIL` covers. So the body is decoded as it arrives and handed over as
	 * it grows: sound starts on the first half megabyte, and every later swap is
	 * exact rather than a crossfade over a guess.
	 */
	async function download(id: string, onPart?: (buffer: AudioBuffer, partial: boolean) => void) {
		for (let attempt = 0; ; attempt++) {
			try {
				const response = await fetch(downloadUrl(id));
				if (!response.ok) throw new Error(`the audio service returned ${response.status}`);
				// no streams here: take the whole body, as this always did.
				if (!response.body) return await context!.decodeAudioData(await response.arrayBuffer());

				return await stream(response, onPart);
			} catch (error) {
				if (attempt >= 3 || !context) {
					console.error(`audio ${context ? 'gave up' : 'was torn down'}: ${id}`, error);
					return null;
				}
				await new Promise((wait) => setTimeout(wait, 250 * 2 ** attempt));
			}
		}
	}

	/**
	 * The body decoded as it arrives: first at half a megabyte, then at every
	 * doubling of it, so a whole track costs about twice one decode however long it
	 * is and the gap between decodes always outgrows what is left to download.
	 *
	 * Only the track being played asks for the parts. The one downloading behind it
	 * has nothing to start early, so it decodes once, at the end.
	 */
	async function stream(
		response: Response,
		onPart?: (buffer: AudioBuffer, partial: boolean) => void
	) {
		const reader = response.body!.getReader();
		const chunks: Uint8Array[] = [];
		let bytes = 0;
		let next = 512 * 1024;

		for (;;) {
			const { done, value } = await reader.read();
			if (value) {
				chunks.push(value);
				bytes += value.length;
			}
			if (!done && (!onPart || !prefixes || bytes < next)) continue;
			next = bytes * 2;

			// decodeAudioData detaches what it is handed, so every decode gets a flat
			// copy of its own and the body itself stays in `chunks`.
			const whole = new Uint8Array(bytes);
			let at = 0;
			for (const chunk of chunks) {
				whole.set(chunk, at);
				at += chunk.length;
			}

			try {
				const buffer = await context!.decodeAudioData(whole.buffer);
				if (done) return buffer;
				onPart!(buffer, true);
			} catch (error) {
				// a whole body that will not decode is the caller's problem, and so is a
				// player that went away mid-download.
				if (done || !context) throw error;

				// this browser will not decode a truncated stream. Chrome and Safari both
				// do, so this is nobody known — and nothing is wrong with the download:
				// stop asking, and let this track and the ones after it wait for the
				// whole body, the way every track used to.
				console.info('audio: this browser decodes whole files only', error);
				prefixes = false;
			}
		}
	}

	function load(
		id: string,
		onPart?: (buffer: AudioBuffer, partial: boolean) => void
	): Promise<AudioBuffer | null> {
		const held = decoded[key(id)];
		if (held) return Promise.resolve(held);

		// already downloading as the track after this one: it decodes once, at the
		// end, so the track starting now waits for the body — which is no worse than
		// every track was before.
		const running = loading[key(id)];
		if (running) return running;

		const started = download(id, onPart).then((buffer) => {
			delete loading[key(id)];
			if (buffer) decoded[key(id)] = buffer;
			return buffer;
		});
		loading[key(id)] = started;

		return started;
	}

	/** A track nobody spliced into: the first play, a skip, a jump in the queue. */
	async function begin(id: string) {
		splicer?.stop();
		audio.bufferedSeconds = 0;
		// `currentSeconds` is where the bar says we are: zero for a fresh track, and
		// whatever was dragged or carried over from the other engine otherwise. Held
		// rather than applied, so a prefix too short to reach it cannot start the
		// track in the wrong place.
		pending = audio.currentSeconds;

		const buffer = await load(id, (part, partial) => hand(id, part, partial));
		// superseded while it downloaded, or the player went away underneath it
		if (!splicer || wanted !== id) return;
		// a track that will not download is a track that sits there, not one that
		// skips the queue past itself four tracks a second.
		if (!buffer) return;

		hand(id, buffer, false);
		schedule();
	}

	/**
	 * Every decode of the track being played: the first one starts it, each later
	 * one is spliced into the sound already running. The same samples on both sides
	 * of the swap, so there is nothing to fade and nothing to line up.
	 */
	function hand(id: string, buffer: AudioBuffer, partial: boolean) {
		if (!splicer || wanted !== id) return;

		audio.bufferedSeconds = partial ? buffer.duration : current.lengthSeconds;
		// what this decode reaches, which is short of its own end while it is a prefix
		const reach = partial ? Math.max(buffer.duration - TAIL, 0) : buffer.duration;

		if (splicer.key !== key(id)) {
			// nothing decoded this far yet: wait for a longer prefix rather than start
			// the track somewhere it is not meant to be. The whole body always starts
			// it — a position past the end of that one is the seek bar's to clamp.
			if (partial && pending > reach) return;

			splicer.start({ key: key(id), buffer, partial }, pending + latency(), !audio.paused);
			pending = 0;
			return;
		}

		splicer.upgrade(buffer, partial);
		// a seek that landed past what was decoded then, honoured now that it is here
		if (pending && pending <= reach) {
			splicer.seek(pending + latency());
			pending = 0;
		}
	}

	/** The sound is already on the next track; the queue and the display catch up. */
	function advance() {
		// only ever the track after this one is queued, and the effect below
		// unqueues the moment that stops being true, so `nextTrack` lands on the
		// track the sound is already playing.
		queue.nextTrack();
		// `setCurrent` zeroes the display, which the seek effect would otherwise
		// read as someone dragging the bar back to the start of the new track.
		audio.currentSeconds = position();
	}

	function schedule() {
		const next = queue.items[queue.currentIndex + 1]?.id;
		if (!splicer || !next) return;

		const buffer = decoded[key(next)];
		if (buffer) splicer.queue({ key: key(next), buffer });
	}

	// Ordered before the seek effect: a track change writes the position to zero,
	// and this has to have moved the splicer onto the new track before that zero
	// is measured against a position on the old one.
	$effect(() => {
		const id = current.id;
		if (!splicer || !id || id === wanted) return;

		wanted = id;
		void begin(id);
	});

	$effect(() => {
		const paused = audio.paused;
		if (!splicer) return;
		if (paused) return splicer.pause();

		// a suspended graph is silence whatever the nodes do, and it starts
		// suspended until a gesture resumes it.
		void context?.resume();
		splicer.resume();
		// pausing dropped the join, because it was scheduled against a clock the
		// sound is no longer riding.
		schedule();
	});

	// a write to the position this component did not make is a seek. The tolerance
	// is what stops its own 10 Hz reports from bouncing back in as one — which
	// needs the target back in the graph's domain first, or every report reads as
	// a seek the moment the output path buffers deeper than the tolerance.
	$effect(() => {
		const seconds = audio.currentSeconds;
		if (!splicer?.key) return;
		if (Math.abs(position() - seconds) <= 0.25) return;

		// past what has been decoded so far: held until a decode reaches it, rather
		// than clamped to wherever the bytes stopped.
		if (seconds > splicer.decoded) return void (pending = seconds);

		splicer.seek(seconds + latency());
		schedule();
	});

	// What plays after this one: downloaded and decoded while there is nothing
	// else to wait on, then handed its start time. Re-runs on every change to the
	// queue and to the quality, which is exactly when a queued join stops being
	// the right one.
	$effect(() => {
		const next = queue.items[queue.currentIndex + 1]?.id;
		const ahead = next ? key(next) : '';
		if (!splicer) return;

		// the queue moved, or the quality did: what was queued is not what comes
		// next any more, and the bytes for it may be the wrong bytes.
		if (splicer.queued && splicer.queued !== ahead) splicer.unqueue();

		const keep = [splicer.key, ahead];
		for (const stale of Object.keys(decoded)) if (!keep.includes(stale)) delete decoded[stale];

		if (!next) return;
		void load(next).then(schedule);
	});

	$effect(() => {
		if (gain) gain.gain.value = audio.volume;
	});

	// ponytail: 10 Hz, not a frame loop — the same rate and the same reasons as the
	// element engine. This feeds the display only, and every write costs a layout,
	// a paint, a re-raster of the player's backdrop blur and a mediaSession IPC.
	$effect(() => {
		if (audio.paused) return;
		const timer = setInterval(() => (audio.currentSeconds = position()), 100);
		return () => clearInterval(timer);
	});

	// The OS media controls hang off a playing media element, and this engine has
	// none — Web Audio alone gets no media session, so the handlers in
	// Controls.svelte and the metadata in TrackInfo.svelte would go dark on the
	// lock screen and the media keys would stop working. This is the element they
	// hang off: ten seconds of silence, looped, in step with the real playback.
	$effect(() => {
		if (!keeper || !keeperSrc) return;
		if (audio.paused) return keeper.pause();
		// before a gesture the browser refuses, which costs nothing: there is no
		// session to keep until something is playing anyway.
		keeper.play().catch(() => {});
	});

	/** Ten seconds of 8-bit silence as an object URL — built rather than shipped,
	 *  since it is 44 bytes of header and a fill. */
	function silence() {
		const rate = 8000;
		const samples = rate * 10;
		const wav = new ArrayBuffer(44 + samples);
		const view = new DataView(wav);
		const text = (at: number, value: string) => {
			for (let index = 0; index < value.length; index++)
				view.setUint8(at + index, value.charCodeAt(index));
		};

		text(0, 'RIFF');
		view.setUint32(4, 36 + samples, true);
		text(8, 'WAVEfmt ');
		view.setUint32(16, 16, true); // rest of the format chunk
		view.setUint16(20, 1, true); // PCM
		view.setUint16(22, 1, true); // mono
		view.setUint32(24, rate, true);
		view.setUint32(28, rate, true); // bytes a second, which 8-bit mono makes the same
		view.setUint16(32, 1, true); // bytes a frame
		view.setUint16(34, 8, true); // bits a sample
		text(36, 'data');
		view.setUint32(40, samples, true);
		// silence in unsigned 8-bit PCM is the middle of the range, not zero
		new Uint8Array(wav, 44).fill(128);

		return URL.createObjectURL(new Blob([wav], { type: 'audio/wav' }));
	}
</script>

<audio bind:this={keeper} src={keeperSrc} loop preload="auto"></audio>
