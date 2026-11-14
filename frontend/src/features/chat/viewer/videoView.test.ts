import { describe, expect, it } from 'vitest'
import { SPEEDS, bufferedEndAt, clampTime, formatTime, fractionOf, nextSpeed, takesText } from './videoView'

describe('writing a time', () => {
  it.each([
    [0, '0:00'],
    [5, '0:05'],
    [59.9, '0:59'],
    [60, '1:00'],
    [187, '3:07'],
    [599, '9:59'],
    [3599, '59:59'],
    [3600, '1:00:00'],
    [3729, '1:02:09'],
    [36000, '10:00:00'],
  ])('writes %f seconds as %s', (seconds, text) => {
    expect(formatTime(seconds)).toBe(text)
  })

  it.each([NaN, Infinity, -Infinity, -5, 0.4])('writes %f, which is not a time to count, as 0:00', (seconds) => {
    expect(formatTime(seconds)).toBe('0:00')
  })
})

describe('keeping a time inside the video', () => {
  it('leaves a time that is inside as it is, and stops one that is past the end or before the start', () => {
    expect(clampTime(30, 100)).toBe(30)
    expect(clampTime(150, 100)).toBe(100)
    expect(clampTime(-4, 100)).toBe(0)
    expect(clampTime(100, 100)).toBe(100)
  })

  it('has no end to keep a time from when the length is not known', () => {
    expect(clampTime(150, Infinity)).toBe(150)
    expect(clampTime(150, NaN)).toBe(150)
  })

  it('takes what is not a time for the start', () => {
    expect(clampTime(NaN, 100)).toBe(0)
  })
})

describe('how far through the video a time is', () => {
  it('is the share of the length, from 0 to 1', () => {
    expect(fractionOf(25, 100)).toBe(0.25)
    expect(fractionOf(100, 100)).toBe(1)
    expect(fractionOf(120, 100)).toBe(1)
  })

  it.each([[10, 0], [10, NaN], [10, Infinity], [NaN, 100], [-3, 100]])('is 0 when the time is %f and the length %f', (time, duration) => {
    expect(fractionOf(time, duration)).toBe(0)
  })
})

describe('the speed button', () => {
  it('goes through the speeds in order and round again', () => {
    let speed = 0.5
    const seen = [speed]
    for (let i = 0; i < SPEEDS.length; i++) {
      speed = nextSpeed(speed)
      seen.push(speed)
    }

    expect(seen).toEqual([0.5, 1, 1.25, 1.5, 2, 0.5])
  })

  it('goes to normal speed from one that is not in the list', () => {
    expect(nextSpeed(3)).toBe(1)
    expect(nextSpeed(0)).toBe(1)
  })
})

describe('whose a key is', () => {
  it.each([
    [{ tagName: 'INPUT', type: 'text' }, true],
    [{ tagName: 'INPUT', type: 'number' }, true],
    [{ tagName: 'input', type: 'search' }, true],
    [{ tagName: 'INPUT' }, true],
    [{ tagName: 'TEXTAREA' }, true],
    [{ tagName: 'SELECT' }, true],
  ])('belongs to the field for %j, which takes text', (target, expected) => {
    expect(takesText(target)).toBe(expected)
  })

  it.each([
    [{ tagName: 'INPUT', type: 'range' }],
    [{ tagName: 'BUTTON' }],
    [{ tagName: 'VIDEO' }],
    [{ tagName: 'DIV' }],
    [null],
  ])('belongs to the player for %j: a slider keeps only its arrows', (target) => {
    expect(takesText(target)).toBe(false)
  })
})

describe('how far a video is buffered', () => {
  const ranges = (...spans: [number, number][]) => ({ length: spans.length, start: (i: number) => spans[i][0], end: (i: number) => spans[i][1] })

  it('is the end of the buffered part the time is in', () => {
    expect(bufferedEndAt(ranges([0, 30], [60, 90]), 10)).toBe(30)
    expect(bufferedEndAt(ranges([0, 30], [60, 90]), 75)).toBe(90)
  })

  it('counts the edges of a buffered part as inside it', () => {
    expect(bufferedEndAt(ranges([0, 30], [60, 90]), 30)).toBe(30)
    expect(bufferedEndAt(ranges([0, 30], [60, 90]), 60)).toBe(90)
  })

  it('is the time itself where nothing is buffered, so there is nothing ahead of it', () => {
    expect(bufferedEndAt(ranges([0, 30], [60, 90]), 45)).toBe(45)
    expect(bufferedEndAt(ranges(), 12)).toBe(12)
  })
})
