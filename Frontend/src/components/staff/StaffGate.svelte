<script lang="ts">
	import type { Snippet } from 'svelte';
	import { page } from '$app/state';
	import { resolve } from '$app/paths';
	import Skeleton from '$components/Skeleton.svelte';
	import account from '$states/account.svelte';

	/**
	 * The one gate every staff page sits behind, so `/admin` and `/studio` cannot drift into two
	 * sets of rules. Cosmetic: every server behind those pages refuses what this hides anyway.
	 *
	 * `needs` is a permission the page needs. Without it, any staff account passes. `outsider` is
	 * what a signed-in account with no role sees instead of the plain refusal.
	 */
	let {
		needs,
		outsider,
		children
	}: { needs?: string; outsider?: Snippet; children: Snippet } = $props();

	let passed = $derived(account.checked && account.staff && (!needs || account.can(needs)));
	let signIn = $derived(
		`${resolve('/settings/account')}?next=${encodeURIComponent(page.url.pathname + page.url.search)}`
	);

	const line = 'max-w-md text-sm text-fog';
</script>

<!-- a refused visitor learns nothing from the tab; a page that passes names itself -->
<svelte:head>
	{#if !passed}<title>musicrain</title>{/if}
</svelte:head>

{#if !account.signedIn}
	<p class={line}>
		<a href={signIn} class="text-primary-500 underline-offset-4 hover:underline">Sign in</a> to continue.
	</p>
{:else if !account.checked}
	<div aria-busy="true" aria-label="Loading">
		<Skeleton class="h-7 w-32" />
		<Skeleton class="mt-4 h-4 w-72 max-w-full" />
	</div>
{:else if !account.staff}
	{#if outsider}
		{@render outsider()}
	{:else}
		<p class={line}>This page is for staff.</p>
	{/if}
{:else if !passed}
	<p class={line}>
		This needs the <b class="font-mono font-normal text-chalk">{needs}</b> permission. Ask the owner.
	</p>
{:else}
	{@render children()}
{/if}
