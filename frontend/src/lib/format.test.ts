import { describe, expect, it } from 'vitest'
import { formatClock, formatDayLabel, formatFileSize, formatLastSeen, formatListTime } from './format'

// All timestamps here are built in local time (new Date(y, m, d, h, min)) and converted to the ISO
// strings the API sends, so the tests give the same answer in any time zone. "Now" is Sunday
// 11 October 2026, 15:00.
const NOW = new Date(2026, 9, 11, 15, 0)
const LOCALE = 'en-US'

const at = (year: number, month: number, day: number, hour = 12, minute = 0, second = 0) =>
  new Date(year, month - 1, day, hour, minute, second).toISOString()

// The space before AM and PM is a narrow no-break space in newer Unicode data, and \s matches it.
describe('formatClock', () => {
  it('shows the local time of day', () => {
    expect(formatClock(at(2026, 10, 11, 15, 5), LOCALE)).toMatch(/^3:05\sPM$/)
    expect(formatClock(at(2026, 10, 11, 0, 9), LOCALE)).toMatch(/^12:09\sAM$/)
  })
})

describe('formatDayLabel', () => {
  it('says Today and Yesterday', () => {
    expect(formatDayLabel(at(2026, 10, 11, 0, 1), NOW, LOCALE)).toBe('Today')
    expect(formatDayLabel(at(2026, 10, 10, 23, 59), NOW, LOCALE)).toBe('Yesterday')
  })

  it('changes day at local midnight, not after 24 hours', () => {
    const justBeforeMidnight = new Date(2026, 9, 11, 0, 5)
    expect(formatDayLabel(at(2026, 10, 10, 23, 55), justBeforeMidnight, LOCALE)).toBe('Yesterday')
    expect(formatDayLabel(at(2026, 10, 11, 0, 1), justBeforeMidnight, LOCALE)).toBe('Today')
  })

  it('names the weekday for the past week', () => {
    expect(formatDayLabel(at(2026, 10, 8), NOW, LOCALE)).toBe('Thursday')
    expect(formatDayLabel(at(2026, 10, 5), NOW, LOCALE)).toBe('Monday')
  })

  it('gives a date from a week ago, with the year only when it differs', () => {
    expect(formatDayLabel(at(2026, 10, 4), NOW, LOCALE)).toBe('Oct 4')
    expect(formatDayLabel(at(2026, 1, 2), NOW, LOCALE)).toBe('Jan 2')
    expect(formatDayLabel(at(2025, 10, 4), NOW, LOCALE)).toBe('Oct 4, 2025')
  })

  // Two servers' clocks can disagree by a little, so a message can look like it is from the future.
  it('treats a time slightly in the future as today', () => {
    expect(formatDayLabel(at(2026, 10, 11, 15, 1), NOW, LOCALE)).toBe('Today')
    expect(formatDayLabel(at(2026, 10, 12, 0, 1), new Date(2026, 9, 11, 23, 59), LOCALE)).toBe('Today')
  })
})

describe('formatListTime', () => {
  it('shows the time for today, Yesterday, the short weekday for the past week, then the date', () => {
    expect(formatListTime(at(2026, 10, 11, 9, 30), NOW, LOCALE)).toMatch(/^9:30\sAM$/)
    expect(formatListTime(at(2026, 10, 10, 23, 0), NOW, LOCALE)).toBe('Yesterday')
    expect(formatListTime(at(2026, 10, 8), NOW, LOCALE)).toBe('Thu')
    expect(formatListTime(at(2026, 10, 5), NOW, LOCALE)).toBe('Mon')
    expect(formatListTime(at(2026, 10, 4), NOW, LOCALE)).toBe('Oct 4')
    expect(formatListTime(at(2025, 10, 4), NOW, LOCALE)).toBe('Oct 4, 2025')
  })
})

describe('formatLastSeen', () => {
  it('says just now for the last minute', () => {
    expect(formatLastSeen(at(2026, 10, 11, 14, 59, 30), NOW, LOCALE)).toBe('just now')
    expect(formatLastSeen(at(2026, 10, 11, 15, 0), NOW, LOCALE)).toBe('just now')
  })

  // Two servers' clocks can disagree by a little, so a time can look like it is from the future.
  it('treats a time slightly in the future as just now', () => {
    expect(formatLastSeen(at(2026, 10, 11, 15, 2), NOW, LOCALE)).toBe('just now')
  })

  it('counts minutes for the last hour', () => {
    expect(formatLastSeen(at(2026, 10, 11, 14, 59), NOW, LOCALE)).toBe('1 min ago')
    expect(formatLastSeen(at(2026, 10, 11, 14, 58), NOW, LOCALE)).toBe('2 min ago')
    expect(formatLastSeen(at(2026, 10, 11, 14, 1), NOW, LOCALE)).toBe('59 min ago')
  })

  it('gives the time of day for earlier today', () => {
    expect(formatLastSeen(at(2026, 10, 11, 14, 0), NOW, LOCALE)).toMatch(/^today at 2:00\sPM$/)
    expect(formatLastSeen(at(2026, 10, 11, 0, 5), NOW, LOCALE)).toMatch(/^today at 12:05\sAM$/)
  })

  it('names yesterday and changes day at local midnight', () => {
    expect(formatLastSeen(at(2026, 10, 10, 23, 59), NOW, LOCALE)).toMatch(/^yesterday at 11:59\sPM$/)

    // 01:30 is more than an hour after both: the one before midnight is yesterday, the one after is today.
    const earlyMorning = new Date(2026, 9, 11, 1, 30)
    expect(formatLastSeen(at(2026, 10, 10, 23, 55), earlyMorning, LOCALE)).toMatch(/^yesterday at 11:55\sPM$/)
    expect(formatLastSeen(at(2026, 10, 11, 0, 10), earlyMorning, LOCALE)).toMatch(/^today at 12:10\sAM$/)
  })

  it('counts minutes across midnight while the last hour has not passed', () => {
    const justAfterMidnight = new Date(2026, 9, 11, 0, 5)

    expect(formatLastSeen(at(2026, 10, 10, 23, 55), justAfterMidnight, LOCALE)).toBe('10 min ago')
  })

  it('names the weekday for the rest of the past week', () => {
    expect(formatLastSeen(at(2026, 10, 8, 12, 0), NOW, LOCALE)).toMatch(/^Thursday at 12:00\sPM$/)
  })

  it('gives a date from a week ago, with the year only when it differs', () => {
    expect(formatLastSeen(at(2026, 10, 4, 12, 0), NOW, LOCALE)).toBe('Oct 4')
    expect(formatLastSeen(at(2025, 10, 4, 12, 0), NOW, LOCALE)).toBe('Oct 4, 2025')
  })
})

describe('formatFileSize', () => {
  it('writes small sizes in bytes', () => {
    expect(formatFileSize(0)).toBe('0 B')
    expect(formatFileSize(1023)).toBe('1023 B')
  })

  it('uses units of 1024', () => {
    expect(formatFileSize(1024)).toBe('1 KB')
    expect(formatFileSize(1024 * 1024)).toBe('1 MB')
    expect(formatFileSize(1024 ** 3)).toBe('1 GB')
  })

  it('shows one decimal below ten of a unit and none above', () => {
    expect(formatFileSize(1536)).toBe('1.5 KB')
    expect(formatFileSize(9.4 * 1024)).toBe('9.4 KB')
    expect(formatFileSize(10 * 1024)).toBe('10 KB')
    expect(formatFileSize(512 * 1024)).toBe('512 KB')
    expect(formatFileSize(10 * 1024 * 1024)).toBe('10 MB')
  })

  it('does not write a trailing .0', () => {
    expect(formatFileSize(2 * 1024 * 1024)).toBe('2 MB')
  })

  it('rolls over to the next unit instead of showing 1024', () => {
    expect(formatFileSize(1024 * 1024 - 1)).toBe('1 MB')
    expect(formatFileSize(1024 * 1024 * 1024 - 1)).toBe('1 GB')
  })
})
