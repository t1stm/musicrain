<script lang="ts">
	import type { Snapshot } from '$requests/admin';

	/** A target with no view of its own: its snapshot as JSON, which Oko always collected and never showed. */
	let { snapshot }: { snapshot: Snapshot } = $props();
</script>

<p class="mb-3 text-sm text-fog">No state of its own — see the Requests tab.</p>
<pre class="admin-scroll max-h-[32rem] p-3 font-mono text-xs leading-relaxed text-chalk">{JSON.stringify(snapshot, null, 2)}</pre>
