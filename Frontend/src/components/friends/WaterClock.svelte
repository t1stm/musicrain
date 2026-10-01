<script lang="ts">
	/**
	 * The gate's rain as a clock for a friend code: fifteen drops, one per minute of the
	 * code's life. Each minute the leftmost hanging drop falls through the line; `spent`
	 * lets every drop still hanging fall at once. The styles are the gate's, in app.css.
	 */
	let { left, spent = false }: { left: number; spent?: boolean } = $props();

	const drops = Array.from({ length: 15 }, (_, index) => index);
	let standing = $derived(spent ? 0 : Math.ceil(left / 60));
</script>

<div class="gate-rain" aria-hidden="true" data-spent={spent || undefined}>
	{#each drops as index (index)}
		{@const fallen = index < drops.length - standing}
		<!-- the gate's columns, kept in from the edges so the first and last marks are whole -->
		<span
			class="gate-drop"
			data-fallen={fallen || undefined}
			style:--i={index}
			style:--hang={18 + ((index * 11) % 26)}
			style:left="{6 + index * (88 / 14)}%"
		></span>
		<span class="gate-wet" data-wet={fallen || undefined} style:left="{6 + index * (88 / 14)}%"
		></span>
	{/each}
</div>
