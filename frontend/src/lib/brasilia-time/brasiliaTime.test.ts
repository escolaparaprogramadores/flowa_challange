import { describe, expect, it } from 'vitest';
import { formatInstantInBrasiliaTime } from './brasiliaTime';

describe('formatInstantInBrasiliaTime', () => {
  it('CA-36: 23:30 UTC shows on the previous day, at 20:30 Brasília time', () => {
    expect(formatInstantInBrasiliaTime('2026-10-04T23:30:00Z')).toEqual({ dayMonthYear: '04/10/2026', hourMinute: '20:30' });
  });

  it('CA-36: 01:15 UTC on the 5th is still the 4th in Brasília, at 22:15', () => {
    expect(formatInstantInBrasiliaTime('2026-10-05T01:15:00Z')).toEqual({ dayMonthYear: '04/10/2026', hourMinute: '22:15' });
  });

  it('CA-36: 03:00 UTC is midnight in Brasília, already on the new day', () => {
    expect(formatInstantInBrasiliaTime('2026-10-05T03:00:00Z')).toEqual({ dayMonthYear: '05/10/2026', hourMinute: '00:00' });
  });

  it('CA-36: the UTC new year is still the previous year in Brasília', () => {
    expect(formatInstantInBrasiliaTime('2027-01-01T02:59:00Z')).toEqual({ dayMonthYear: '31/12/2026', hourMinute: '23:59' });
  });

  it('ASSUMI-08: accepts the real server receivedAt, with microseconds', () => {
    expect(formatInstantInBrasiliaTime('2026-10-04T09:34:23.390427Z')).toEqual({ dayMonthYear: '04/10/2026', hourMinute: '06:34' });
  });

  it('a receivedAt out of format becomes "—" without breaking the list', () => {
    expect(formatInstantInBrasiliaTime('not an instant')).toEqual({ dayMonthYear: '—', hourMinute: '' });
  });
});
