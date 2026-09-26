<script lang="ts">
	import quality, { bitrates, codecs, type Bitrate, type Codec } from '$states/quality.svelte';
	import { closeOnBack } from '$lib/backWatcher.svelte';
	import { dismiss } from '$lib/dismiss';

	let open = $state(false);

	closeOnBack(
		() => open,
		() => (open = false)
	);

	function selectCodec(codec: Codec) {
		quality.codec = codec;
	}

	function selectBitrate(bitrate: Bitrate) {
		quality.bitrate = bitrate;
	}
</script>

<div id="player-quality" class="relative" {@attach open && dismiss(() => (open = false))}>
	<button type="button" class="whitespace-nowrap rounded-art border border-haze px-1.5 py-1 font-mono text-[10px] uppercase tracking-[0.08em] text-fog hover:border-primary-0 hover:text-chalk" aria-expanded={open} aria-label="Choose audio format" onclick={() => (open = !open)}
		><span>{quality.codec}</span>{#if quality.codec !== 'FLAC'}<span>&nbsp;·&nbsp;</span><span>{quality.bitrate}</span>{/if}</button
	>
	{#if open}
		<div class="absolute bottom-full right-0 z-30 mb-2 w-60 rounded-panel sm:w-52 border border-haze bg-surface-100 p-2">
			<p class="eyebrow mb-1 px-1">Format</p>
			<div class="grid grid-cols-3 gap-1">
				{#each codecs as codec (codec)}
					<button type="button" class:bg-primary-600={quality.codec === codec} class="rounded-art px-1.5 py-2.5 text-xs sm:py-1 text-chalk hover:bg-surface-200" onclick={() => selectCodec(codec)}>{codec}</button>
				{/each}
			</div>
			{#if quality.codec !== 'FLAC'}
				<p class="eyebrow mb-1 mt-3 px-1">Bitrate</p>
				<div class="grid grid-cols-5 gap-1">
					{#each bitrates as bitrate (bitrate)}
						<button type="button" class:bg-primary-600={quality.bitrate === bitrate} class="rounded-art px-1 py-2.5 font-mono sm:py-1 text-[10px] text-chalk hover:bg-surface-200" onclick={() => selectBitrate(bitrate)}>{bitrate}</button>
					{/each}
				</div>
			{/if}
		</div>
	{/if}
</div>
