import { describe, expect, it } from 'vitest';
import { effectiveStatus, isExpired, isValidStudentId, remainingTime } from './appLogic';

describe('admission UI rules', () => {
  it('accepts only 17 digit student identifiers', () => {
    expect(isValidStudentId('20152691234567890')).toBe(true);
    expect(isValidStudentId('123')).toBe(false);
    expect(isValidStudentId('2015269123456789a')).toBe(false);
    expect(isValidStudentId('201526912345678901')).toBe(false);
    expect(isValidStudentId(' 2015269123456789')).toBe(false);
    expect(isValidStudentId(null)).toBe(false);
  });

  it('marks a hold expired at or after its expiration time', () => {
    const now = new Date('2026-11-10T10:00:00Z');
    expect(isExpired('2026-11-10T09:59:59Z', now)).toBe(true);
    expect(isExpired('2026-11-10T10:00:00Z', now)).toBe(true);
    expect(isExpired('2026-11-10T10:00:01Z', now)).toBe(false);
    expect(isExpired('not-a-date', now)).toBe(true);
  });

  it('formats the remaining time until expiration', () => {
    const now = new Date('2026-11-10T10:00:00Z');
    expect(remainingTime('2026-11-10T11:05:00Z', now)).toBe('1h 5m');
    expect(remainingTime('2026-11-10T10:02:30Z', now)).toBe('2m 30s');
    expect(remainingTime('2026-11-10T10:00:45Z', now)).toBe('45s');
    expect(remainingTime('2026-11-10T09:59:59Z', now)).toBe(null);
  });

  it('presents an overdue HELD hold as EXPIRED', () => {
    const now = new Date('2026-11-10T10:00:00Z');
    expect(effectiveStatus({ status: 'HELD', expiresAt: '2026-11-10T09:59:59Z' }, now)).toBe('EXPIRED');
    expect(effectiveStatus({ status: 'HELD', expiresAt: '2026-11-10T10:00:01Z' }, now)).toBe('HELD');
    expect(effectiveStatus({ status: 'CONFIRMED', expiresAt: '2026-11-10T09:00:00Z' }, now)).toBe('CONFIRMED');
  });
});
