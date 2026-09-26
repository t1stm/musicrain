<script lang="ts">
	import { resolve } from '$app/paths';
	import session from '$states/session.svelte';

	let draft = $state('');
	let log = $state<HTMLElement | null>(null);

	// follow the tail as lines arrive
	$effect(() => {
		const lines = session.chat.length;
		if (log && lines > 0) log.scrollTop = log.scrollHeight;
	});

	function send(event: SubmitEvent) {
		event.preventDefault();
		const text = draft.trim();
		if (!text) return;
		session.send(`chat ${text}`);
		draft = '';
	}
</script>

{#if !session.inRoom}
	<div class="flex flex-1 flex-col items-start justify-center gap-4 p-4">
		<h3 class="font-display text-base font-normal text-chalk">You’re listening alone.</h3>
		<p class="text-sm text-fog">
			Rooms play one queue in time with everyone in them, and chat lives there.
		</p>
		<a
			href={resolve('/rooms')}
			class="rounded-row bg-primary-600 px-3 py-2 text-sm font-semibold text-white hover:bg-primary-0 focus-visible:outline-2 focus-visible:outline-primary-200"
		>
			Browse rooms
		</a>
		<p class="eyebrow">or start a room</p>
	</div>
{:else}
	<div bind:this={log} class="min-h-0 flex-1 space-y-3 overflow-y-auto py-3 pr-1">
		{#if session.chat.length === 0}
			<p class="text-sm text-fog">Nothing said yet. Chat starts when you do.</p>
		{/if}
		{#each session.chat as line (line.id)}
			{#if line.system}
				<p class="text-xs text-fog"><span class="eyebrow">System</span> {line.text}</p>
			{:else}
				<p class="text-sm break-words">
					<span class="font-semibold text-primary-500">{line.username}</span>
					<span class="font-mono text-[0.68rem] text-fog">{line.at}</span><br />
					{line.text}
				</p>
			{/if}
		{/each}
	</div>

	<form class="flex gap-2 border-t border-haze pt-3" onsubmit={send}>
		<input
			type="text"
			bind:value={draft}
			maxlength="500"
			enterkeyhint="send"
			placeholder="Message the room"
			aria-label="Message the room"
			class="rounded-row border border-haze bg-dark-0 w-full text-sm text-chalk placeholder:text-fog ring-primary-0 focus:border-primary-0 focus-visible:ring-2"
		/>
	</form>
{/if}
