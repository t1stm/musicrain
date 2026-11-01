<script lang="ts">
	/** A target's headline numbers. `bar` is a percentage, for bytes against a maximum. */
	let { items }: { items: { value: string | number; label: string; bar?: number }[] } = $props();
</script>

<dl class="mb-4 flex flex-wrap gap-2.5">
	{#each items as item (item.label)}
		<div class="min-w-24 rounded-panel border border-haze bg-surface-0 px-3.5 py-2.5">
			<dd class="font-mono text-lg leading-tight text-chalk">
				{typeof item.value === 'number' ? item.value.toLocaleString() : item.value}
			</dd>
			<dt class="eyebrow mt-0.5">{item.label}</dt>
			{#if item.bar !== undefined}
				<div class="mt-1.5 h-1 overflow-hidden rounded-full bg-haze">
					<div class="h-full bg-primary-0" style="width: {Math.min(100, item.bar).toFixed(1)}%"></div>
				</div>
			{/if}
		</div>
	{/each}
</dl>
