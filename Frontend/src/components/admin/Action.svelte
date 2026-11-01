<script lang="ts">
	import Ask from './Ask.svelte';
	import type { Params } from '$requests/admin';
	import fleet from '$states/fleet.svelte';

	/**
	 * The one button every view acts through. Without the target's permission it stays on screen,
	 * disabled, with a title naming what to ask for: a hidden button hides the very thing an admin
	 * needs to know. Oko refuses it anyway, and audits the attempt.
	 */
	let {
		target,
		action,
		params = {},
		label,
		confirm,
		ask,
		danger = false,
		disabled = false,
		title,
		ondone
	}: {
		target: string;
		action: string;
		params?: Params;
		label: string;
		/** A sentence to agree with first. */
		confirm?: string;
		/** A value to prompt for, sent as `name`. */
		ask?: { name: string; message: string; kind: 'text' | 'password'; initial?: string; minLength?: number };
		danger?: boolean;
		/** For a reason of the view's own; `title` then says what it is. */
		disabled?: boolean;
		title?: string;
		ondone?: (result: unknown) => void;
	} = $props();

	let asking = $state(false);
	let busy = $state(false);
	let error = $state('');
	let allowed = $derived(fleet.may(target));

	function click() {
		error = '';
		if (confirm || ask) asking = true;
		else run();
	}

	async function run(value?: string) {
		let sent = params;
		if (ask && value !== undefined) {
			const given = params instanceof URLSearchParams ? [...params] : Object.entries(params).map(([k, v]) => [k, String(v)]);
			sent = new URLSearchParams([...given.filter(([key]) => key !== ask.name), [ask.name, value]]);
		}
		busy = true;
		try {
			// not `ondone?.(await …)`: an optional call skips its arguments, the action with them
			const result = await fleet.act(target, action, sent);
			ondone?.(result);
		} catch (failure) {
			error = failure instanceof Error ? failure.message : 'The action failed.';
		} finally {
			busy = false;
		}
	}
</script>

<button
	type="button"
	class="admin-button"
	data-danger={danger || undefined}
	disabled={!allowed || disabled || busy}
	title={!allowed ? fleet.needs(target) : title}
	aria-busy={busy}
	onclick={click}
>
	{label}
</button>
{#if error}<span class="block max-w-64 py-0.5 text-xs whitespace-normal text-ember" role="alert">{error}</span>{/if}

<Ask
	bind:open={asking}
	message={ask?.message ?? confirm ?? ''}
	ask={ask && { kind: ask.kind, label: ask.kind === 'password' ? 'New password' : 'New value', minLength: ask.minLength }}
	initial={ask?.initial ?? ''}
	confirmLabel={label}
	{danger}
	onanswer={(value) => run(ask ? value : undefined)}
/>
