import { error } from '@sveltejs/kit';
import type { PageLoad } from './$types';

/** The lyrics studio is a tool for this machine, run with `npm run dev`. A release has no studio. */
export const load: PageLoad = () => {
	if (!import.meta.env.DEV) error(404, 'Not found');
};
