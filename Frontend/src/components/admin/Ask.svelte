<script lang="ts">
	import { closeOnBack } from '$lib/backWatcher.svelte';

	/**
	 * The panel's one confirm-or-prompt, as an in-app dialog. Never `window.confirm` or `prompt`:
	 * they block the page, and they block the browser harness the e2e checks drive it with.
	 * Back, the back gesture and Escape cancel, as in every other gate.
	 */
	let {
		open = $bindable(false),
		message,
		ask,
		initial = '',
		confirmLabel = 'Go ahead',
		danger = false,
		onanswer
	}: {
		open?: boolean;
		message: string;
		/** A value to type: its kind decides the field. Without it this is a plain confirm. */
		ask?: { kind: 'text' | 'password'; label: string; minLength?: number };
		initial?: string;
		confirmLabel?: string;
		danger?: boolean;
		/** Only on Go ahead, with the typed value when there is a field. */
		onanswer: (value: string) => void;
	} = $props();

	let dialog = $state<HTMLDialogElement>();
	let value = $state('');
	let short = $derived(!!ask?.minLength && value.length < ask.minLength);

	$effect(() => {
		if (open && dialog && !dialog.open) {
			value = initial;
			dialog.showModal();
		}
	});

	closeOnBack(
		() => open,
		() => dialog?.close()
	);

	function submit(event: SubmitEvent) {
		event.preventDefault();
		if (ask && (!value.trim() || short)) return;
		dialog?.close();
		onanswer(value);
	}

	const button = 'min-h-11 rounded-row px-4 text-sm font-semibold sm:min-h-9';
</script>

{#if open}
	<dialog bind:this={dialog} class="gate" closedby="any" onclose={() => (open = false)}>
		<form class="p-5 sm:p-6" onsubmit={submit}>
			<p class="text-[0.9375rem] leading-relaxed whitespace-pre-line text-chalk">{message}</p>
			{#if ask}
				<label class="mt-4 block text-[0.8125rem] text-fog" for="ask-value">{ask.label}</label>
				<!-- svelte-ignore a11y_autofocus -->
				<input
					id="ask-value"
					type={ask.kind}
					bind:value
					autofocus
					autocomplete={ask.kind === 'password' ? 'new-password' : 'off'}
					autocapitalize="none"
					spellcheck="false"
					class="mt-1.5 min-h-11 w-full rounded-row border border-haze bg-dark-0 font-mono text-sm text-chalk ring-primary-0 focus:border-primary-0 focus-visible:ring-2 sm:min-h-9"
				/>
				{#if ask.minLength}
					<p class="mt-1 text-[0.8125rem] {short && value ? 'text-ember' : 'text-fog'}">
						At least {ask.minLength} characters.
					</p>
				{/if}
			{/if}
			<div class="mt-5 flex justify-end gap-2">
				<button
					type="button"
					class="{button} border border-haze text-chalk hover:bg-surface-200"
					onclick={() => dialog?.close()}>Cancel</button
				>
				<button
					type="submit"
					disabled={!!ask && (!value.trim() || short)}
					class="{button} disabled:opacity-60 {danger
						? 'border border-ember text-ember hover:bg-surface-200'
						: 'bg-primary-600 text-white hover:bg-primary-0'}">{confirmLabel}</button
				>
			</div>
		</form>
	</dialog>
{/if}
