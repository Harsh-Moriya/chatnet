// The small rules of the video viewer: how time is written, how far a video is buffered, and how fast it plays. Pure, so they are tested
// without a browser.

// How far J and L move the video, in seconds.
export const SEEK_STEP_SECONDS = 10

// How long the controls stay after the last movement while the video plays.
export const CONTROLS_HIDE_MS = 3000

// The speeds the speed button goes through, in order.
export const SPEEDS = [0.5, 1, 1.25, 1.5, 2] as const

const usable = (seconds: number) => Number.isFinite(seconds) && seconds > 0

// A time as a person reads it: 3:07, or 1:02:09 once it is an hour or more. Nothing that is not a time is 0:00.
export function formatTime(seconds: number): string {
  const whole = usable(seconds) ? Math.floor(seconds) : 0
  const hours = Math.floor(whole / 3600)
  const minutes = Math.floor((whole % 3600) / 60)
  const rest = String(whole % 60).padStart(2, '0')

  return hours > 0 ? `${hours}:${String(minutes).padStart(2, '0')}:${rest}` : `${minutes}:${rest}`
}

// A time kept inside the video. A video whose length is not known (a recording that was never finished) has no end to keep it from.
export function clampTime(time: number, duration: number): number {
  if (!usable(time)) return 0
  return Number.isFinite(duration) ? Math.min(time, Math.max(0, duration)) : time
}

// How far through the video a time is, from 0 to 1.
export function fractionOf(time: number, duration: number): number {
  if (!usable(time) || !usable(duration) || !Number.isFinite(duration)) return 0
  return Math.min(1, time / duration)
}

// The speed after this one. A speed that is not in the list goes to normal speed.
export function nextSpeed(current: number): number {
  const index = SPEEDS.indexOf(current as (typeof SPEEDS)[number])
  return index < 0 ? 1 : SPEEDS[(index + 1) % SPEEDS.length]
}

// Whether a key pressed in an element is its own and not the player's: in a field that takes text, yes. A slider (the seek bar, the volume)
// keeps only its arrow keys, so that after a click on the seek bar the player's keys still work.
export function takesText(target: { tagName?: string; type?: string } | null): boolean {
  const tag = (target?.tagName ?? '').toUpperCase()
  if (tag === 'TEXTAREA' || tag === 'SELECT') return true
  return tag === 'INPUT' && (target?.type ?? 'text') !== 'range'
}

// What a video element says about what it has loaded: ranges of time, each from `start(i)` to `end(i)`.
export interface TimeRangesLike {
  length: number
  start: (index: number) => number
  end: (index: number) => number
}

// Where the buffered part that holds a time ends: how far the video can play from there without waiting. A time that is not buffered has
// nothing ahead of it, so it is the time itself.
export function bufferedEndAt(ranges: TimeRangesLike, time: number): number {
  for (let index = 0; index < ranges.length; index++) {
    if (ranges.start(index) <= time && time <= ranges.end(index)) return ranges.end(index)
  }
  return time
}
