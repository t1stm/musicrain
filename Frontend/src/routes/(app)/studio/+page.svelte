<script lang="ts">
	import StaffGate from '$components/staff/StaffGate.svelte';
	import Studio from '$components/studio/Studio.svelte';
</script>

<!-- the scroller is here so the gate's refusals sit in it as well as the studio -->
<div class="page page-column gap-6 px-4 pt-6 pb-4 sm:px-8 sm:pb-28">
	<StaffGate needs="studio">
		<Studio />
	</StaffGate>
</div>
