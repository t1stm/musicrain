import { describe, expect, it } from 'vitest';
import { convertTimeSpanStringToSeconds, getTimeString } from './time';

describe('convertTimeSpanStringToSeconds', () => {
	it('reads a track', () => {
		expect(convertTimeSpanStringToSeconds('00:03:45')).toBe(225);
	});

	it('counts the days a long playlist runs to', () => {
		expect(convertTimeSpanStringToSeconds('4.09:12:09')).toBe(4 * 86400 + 9 * 3600 + 12 * 60 + 9);
		expect(getTimeString(convertTimeSpanStringToSeconds('4.09:12:09'))).toBe('4d 9:12:09');
	});

	it('says a whole day, and an hour, as such', () => {
		expect(getTimeString(86400)).toBe('1d 0:00:00');
		expect(getTimeString(86399)).toBe('23:59:59');
		expect(getTimeString(3600)).toBe('1:00:00');
		expect(getTimeString(225)).toBe('3:45');
	});

	it('drops the fraction of a second', () => {
		expect(convertTimeSpanStringToSeconds('00:03:33.5000000')).toBe(213);
	});
});
