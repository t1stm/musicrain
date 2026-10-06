export function getTimeString(seconds: number) {
	const totalSeconds = Math.max(0, Math.floor(Number.isFinite(seconds) ? seconds : 0));
	const days = Math.floor(totalSeconds / 86400);
	const hours = Math.floor((totalSeconds % 86400) / 3600);
	const minutes = Math.floor((totalSeconds % 3600) / 60);
	const remainingSeconds = totalSeconds % 60;
	const pad = (value: number) => String(value).padStart(2, '0');

	// past a day only a playlist's total, or a queue's: 4d 9:12:09 reads, 105:12:09 has to be worked out
	if (days > 0) return `${days}d ${hours}:${pad(minutes)}:${pad(remainingSeconds)}`;
	return hours > 0 ? `${hours}:${pad(minutes)}:${pad(remainingSeconds)}` : `${minutes}:${pad(remainingSeconds)}`;
}

/**
 * .NET's TimeSpan "c" shape, `[d.]hh:mm:ss[.fffffff]`. A playlist's total is the one that runs
 * past a day, and its days come before a dot, not a colon: `4.09:12:09` is 105 hours, not 4.
 */
export function convertTimeSpanStringToSeconds(dateString: string): number {
	const [, days = '0', clock = dateString] = dateString.match(/^(\d+)\.(\d+:\d+:.*)$/) ?? [];

	return (
		Number.parseInt(days) * 86400 +
		clock
			.split(':')
			.reverse()
			.reduce((previous, current, i) => previous + Number.parseInt(current) * Math.pow(60, i), 0)
	);
}
