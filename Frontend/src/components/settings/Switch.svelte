<script lang="ts">
	// An on/off setting. Goes inside the `label` that names it, so the whole row is the target.
	let { checked = $bindable() }: { checked: boolean } = $props();
</script>

<input
	type="checkbox"
	role="switch"
	bind:checked
	class="switch relative h-6 w-10 shrink-0 cursor-pointer appearance-none rounded-full border-0 bg-surface-300 transition-colors checked:bg-primary-600 focus:ring-0 focus:ring-offset-0 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-200 motion-reduce:transition-none"
/>

<style>
	/* The thumb. `@tailwindcss/forms` paints a checked box with a tick; a switch is not a box. */
	.switch {
		background-image: none;
	}
	.switch::after {
		content: '';
		position: absolute;
		top: 2px;
		left: 2px;
		width: 20px;
		height: 20px;
		border-radius: 9999px;
		background: var(--color-chalk);
		transition: translate 150ms cubic-bezier(0.2, 0.7, 0.3, 1);
	}
	.switch:checked::after {
		translate: 16px 0;
	}
	@media (prefers-reduced-motion: reduce) {
		.switch::after {
			transition: none;
		}
	}
</style>
