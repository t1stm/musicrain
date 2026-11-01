<script lang="ts">
	import { claim } from '$requests/staff';
	import account from '$states/account.svelte';

	// What a signed-in account with no role sees on /admin. Claiming the key changes the role, and
	// the gate around this opens on its own once `refresh` has read it.
	let typed = $state('');
	let busy = $state(false);
	let error = $state('');

	// Copied out of a terminal, a key can arrive wrapped or with a line end: none of that is the key.
	let key = $derived(typed.replace(/\s+/g, ''));

	async function submit(event: SubmitEvent) {
		event.preventDefault();
		if (!key || busy) return;

		busy = true;
		error = '';
		try {
			await claim(key);
			typed = '';
			await account.refresh();
		} catch (failure) {
			error = failure instanceof Error ? failure.message : 'Could not reach the audio service.';
		} finally {
			busy = false;
		}
	}

	/** Enter uses the key, as it would in a one-line field. The textarea only exists to show all 64 at once. */
	function enter(event: KeyboardEvent) {
		if (event.key !== 'Enter' || event.shiftKey) return;
		event.preventDefault();
		(event.currentTarget as HTMLTextAreaElement).form?.requestSubmit();
	}
</script>

<section class="max-w-md">
	<h1 class="font-display text-lg leading-tight font-light tracking-tight text-chalk sm:text-2xl">
		Admin
	</h1>
	<p class="mt-2 text-sm text-fog">
		This page is for staff. If you were given an administrator key, enter it to join.
	</p>

	<form class="key mt-6 flex flex-col gap-2" onsubmit={submit}>
		<div class="flex items-baseline justify-between gap-3">
			<label for="admin-key" class="text-[0.8125rem] text-fog">Administrator key</label>
			<!-- whether the paste caught all of it, before Dom is asked -->
			<span
				class="font-mono text-xs {key.length === 64 ? 'text-primary-500' : 'text-fog'}"
				aria-live="polite">{key.length} / 64</span
			>
		</div>
		<textarea
			id="admin-key"
			bind:value={typed}
			oninput={() => (error = '')}
			onkeydown={enter}
			rows="2"
			autocomplete="off"
			autocapitalize="none"
			spellcheck="false"
			enterkeyhint="go"
			placeholder="64 characters, 0–9 and a–f"
			class="w-full resize-none rounded-row border border-haze bg-dark-0 font-mono text-sm text-chalk placeholder:text-surface-400 ring-primary-0 focus:border-primary-0 focus-visible:ring-2"
		></textarea>
		{#if error}<p class="text-sm text-ember" role="alert">{error}</p>{/if}
		<button
			type="submit"
			disabled={!key || busy}
			class="mt-1 min-h-11 w-fit rounded-row bg-primary-600 px-4 text-sm font-semibold text-white hover:bg-primary-0 disabled:opacity-60 sm:min-h-9"
		>
			{busy ? 'Checking…' : 'Use key'}
		</button>
	</form>

	<p class="mt-6 text-[0.8125rem] leading-normal text-fog">
		Dom prints the key in its log when it makes one, and keeps it in
		<code class="font-mono text-chalk">/data/admin.key</code> on the server until someone uses it. It works
		once.
	</p>
</section>

<style>
	/* 64 characters wrap as two even lines of 32, so the whole key can be checked at a glance.
	   The form takes the field's width, so the count sits over the field's own edge. */
	.key {
		/* 32 advances at the field's size, its padding and its border */
		max-width: calc(32 * (0.875rem * 0.6 + 0.04em) + 1.5rem + 2px);
	}

	.key textarea {
		word-break: break-all;
		letter-spacing: 0.04em;
		line-height: 1.6;
	}
</style>
