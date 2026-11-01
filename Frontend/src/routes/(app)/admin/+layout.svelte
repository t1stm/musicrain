<script lang="ts">
	import Shell from '$components/admin/Shell.svelte';
	import KeyForm from '$components/staff/KeyForm.svelte';
	import StaffGate from '$components/staff/StaffGate.svelte';

	let { children } = $props();
</script>

<div class="page p-4 sm:p-6 sm:pb-28">
	<div class="mx-auto w-full max-w-6xl">
		<StaffGate>
			{#snippet outsider()}
				<KeyForm />
			{/snippet}
			<Shell>{@render children()}</Shell>
		</StaffGate>
	</div>
</div>
