// Display formatting for the timestamps the API sends. Everything is in the viewer's local time:
// "today" and "yesterday" are calendar days where the person is, not 24-hour periods.
//
// `now` and `locale` are parameters so the functions are predictable under test; components pass
// the current time and leave the locale to the browser.

const DAY_MS = 86_400_000

const startOfDay = (date: Date) => new Date(date.getFullYear(), date.getMonth(), date.getDate())

// Whole calendar days between a moment and now. A moment a little ahead of this browser's clock
// (the server's clock can run ahead) counts as today instead of going negative.
function daysAgo(then: Date, now: Date): number {
  const days = Math.round((startOfDay(now).getTime() - startOfDay(then).getTime()) / DAY_MS)
  return Math.max(0, days)
}

// "Oct 4", or "Oct 4, 2025" when the year is not the current one.
function shortDate(then: Date, now: Date, locale?: string): string {
  return then.toLocaleDateString(locale, {
    month: 'short',
    day: 'numeric',
    ...(then.getFullYear() !== now.getFullYear() ? { year: 'numeric' as const } : {}),
  })
}

export function formatClock(iso: string, locale?: string): string {
  return new Date(iso).toLocaleTimeString(locale, { hour: 'numeric', minute: '2-digit' })
}

// The heading of a day in a conversation: Today, Yesterday, the weekday for the rest of the past
// week, then the date.
export function formatDayLabel(iso: string, now: Date, locale?: string): string {
  const then = new Date(iso)
  const days = daysAgo(then, now)

  if (days === 0) return 'Today'
  if (days === 1) return 'Yesterday'
  if (days < 7) return then.toLocaleDateString(locale, { weekday: 'long' })
  return shortDate(then, now, locale)
}

// The short time in the sidebar: the clock for today, then Yesterday, a short weekday, a date.
export function formatListTime(iso: string, now: Date, locale?: string): string {
  const then = new Date(iso)
  const days = daysAgo(then, now)

  if (days === 0) return formatClock(iso, locale)
  if (days === 1) return 'Yesterday'
  if (days < 7) return then.toLocaleDateString(locale, { weekday: 'short' })
  return shortDate(then, now, locale)
}
