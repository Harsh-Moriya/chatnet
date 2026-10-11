import { describe, expect, it } from 'vitest'
import { formatClock, formatDayLabel, formatListTime } from './format'

// All timestamps here are built in local time (new Date(y, m, d, h, min)) and converted to the ISO
// strings the API sends, so the tests give the same answer in any time zone. "Now" is Sunday
// 11 October 2026, 15:00.
const NOW = new Date(2026, 9, 11, 15, 0)
const LOCALE = 'en-US'

const at = (year: number, month: number, day: number, hour = 12, minute = 0) =>
  new Date(year, month - 1, day, hour, minute).toISOString()

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
