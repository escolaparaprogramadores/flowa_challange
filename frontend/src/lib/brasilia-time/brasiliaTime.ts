// The list shows Brasília time whatever the time zone of whoever opens the screen.
const BRASILIA_TIME_ZONE = 'America/Sao_Paulo';

const brasiliaDayMonthYearFormatter = new Intl.DateTimeFormat('pt-BR', {
  timeZone: BRASILIA_TIME_ZONE,
  day: '2-digit',
  month: '2-digit',
  year: 'numeric',
});

const brasiliaHourMinuteFormatter = new Intl.DateTimeFormat('pt-BR', {
  timeZone: BRASILIA_TIME_ZONE,
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
});

export type InstantInBrasiliaTime = { dayMonthYear: string; hourMinute: string };

export function formatInstantInBrasiliaTime(instantInIsoUtc: string): InstantInBrasiliaTime {
  const receivedInstant = new Date(instantInIsoUtc);
  if (Number.isNaN(receivedInstant.getTime())) return { dayMonthYear: '—', hourMinute: '' };
  return {
    dayMonthYear: brasiliaDayMonthYearFormatter.format(receivedInstant),
    hourMinute: brasiliaHourMinuteFormatter.format(receivedInstant),
  };
}
