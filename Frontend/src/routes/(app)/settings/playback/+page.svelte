<script lang="ts">
	import Switch from '$components/settings/Switch.svelte';
	import SyncToggle from '$components/settings/SyncToggle.svelte';
	import lyrics from '$states/lyrics.svelte';
	import quality, { bitrates, codecs } from '$states/quality.svelte';

	/** Bars rise with log(kbps): 32 is a fifth of the ladder, 320 the whole of it. */
	const height = (kbps: number) => 20 + (80 * Math.log(kbps / 32)) / Math.log(10);

	/** kbps × 3600 s ÷ 8 bits ÷ 1000 — on a phone, this is what "quality" costs. */
	let perHour = $derived(Math.round(quality.bitrate * 0.45));
</script>

<svelte:head><title>Playback · Settings · musicrain</title></svelte:head>

<h1 class="font-display text-lg leading-tight font-light tracking-tight text-chalk sm:text-2xl">
	Playback
</h1>

<section class="mt-6 flex flex-col gap-4">
	<h2 class="eyebrow flex items-center gap-3">
		Streaming quality
		<span class="h-px flex-1 bg-haze"></span>
	</h2>

	<fieldset>
		<legend class="mb-2 text-sm text-chalk">Format</legend>
		<div class="grid grid-cols-5 gap-1 rounded-row border border-haze p-1">
			{#each codecs as codec (codec)}
				<label
					class="relative flex min-h-11 cursor-pointer items-center justify-center rounded-art text-sm text-fog hover:text-chalk has-checked:bg-primary-600 has-checked:text-white has-focus-visible:outline-2 has-focus-visible:outline-primary-200 sm:min-h-9"
				>
					<input type="radio" name="codec" value={codec} bind:group={quality.codec} class="sr-only" />
					{codec}
				</label>
			{/each}
		</div>
	</fieldset>

	{#if quality.codec === 'FLAC'}
		<p class="text-sm text-fog">Lossless. Size follows the file.</p>
	{:else}
		<!-- The ladder: a level meter where every bar up to the chosen rate is lit. Underneath
		     it is a native radio group, so arrow keys and screen readers get it for free. -->
		<fieldset>
			<legend class="mb-2 text-sm text-chalk">Bitrate</legend>
			<div class="flex h-20 items-end gap-1">
				{#each bitrates as kbps (kbps)}
					<label class="flex h-full flex-1 cursor-pointer items-end" title="{kbps} kbps">
						<input
							type="radio"
							name="bitrate"
							value={kbps}
							bind:group={quality.bitrate}
							aria-label="{kbps} kbps"
							class="peer sr-only"
						/>
						<span
							class="w-full rounded-t-art transition-colors duration-120 peer-focus-visible:outline-2 peer-focus-visible:outline-offset-2 peer-focus-visible:outline-primary-200 motion-reduce:transition-none"
							class:bg-primary-600={kbps <= quality.bitrate}
							class:bg-surface-300={kbps > quality.bitrate}
							style:height="{height(kbps)}%"
						></span>
					</label>
				{/each}
			</div>
			<div class="mt-1 flex justify-between font-mono text-[0.68rem] text-fog" aria-hidden="true">
				<span>{bitrates[0]}</span><span>{bitrates[bitrates.length - 1]}</span>
			</div>
		</fieldset>
		<p class="font-mono text-sm text-fog">
			<span class="text-chalk">{quality.bitrate} kbps</span> · ≈ {perHour} MB an hour
		</p>
	{/if}

	<SyncToggle key="quality" />
</section>

<section class="mt-9 flex flex-col gap-4">
	<h2 class="eyebrow flex items-center gap-3">
		Lyrics
		<span class="h-px flex-1 bg-haze"></span>
	</h2>

	<label class="flex min-h-11 cursor-pointer items-center justify-between gap-4 text-sm text-chalk">
		Show lyrics in the full player
		<Switch bind:checked={lyrics.open} />
	</label>
</section>

