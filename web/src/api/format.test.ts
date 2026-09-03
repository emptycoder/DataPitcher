import { describe, expect, it } from 'vitest';
import { formatBytes, formatDuration, formatNumber, formatPercent, formatRelative } from './format';

describe('formatters', () => {
  it('formats numbers, bytes and durations', () => {
    expect(formatNumber(1234567)).toBe('1,234,567');
    expect(formatNumber(null)).toBe('—');
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(1536)).toBe('1.50 KB');
    expect(formatBytes(5 * 1024 * 1024)).toBe('5.00 MB');
    expect(formatDuration(65_000)).toBe('1m 5s');
    expect(formatDuration(3_725_000)).toBe('1h 2m 5s');
    expect(formatPercent(0.4567)).toBe('46%');
    expect(formatPercent(0.05)).toBe('5.0%');
  });

  it('formats relative time', () => {
    const now = Date.parse('2026-09-03T12:00:00Z');
    expect(formatRelative('2026-09-03T11:59:58Z', now)).toBe('just now');
    expect(formatRelative('2026-09-03T11:30:00Z', now)).toBe('30m ago');
    expect(formatRelative('2026-09-02T12:00:00Z', now)).toBe('1d ago');
  });
});
