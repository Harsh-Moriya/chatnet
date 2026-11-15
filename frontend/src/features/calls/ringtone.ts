// The sound of a call that is ringing, made with Web Audio so that there is no file to ship: two tones together (as a telephone rings) in
// bursts, a double burst for a call coming in and a long one for a call going out. A browser that has not let the page make sound yet
// leaves it silent; the screen still shows the call.

export type RingKind = 'incoming' | 'outgoing'

export interface Ringtone {
  start: (kind: RingKind) => void
  stop: () => void
}

// The parts of Web Audio used, so a test can stand in for them.
export interface AudioContextLike {
  readonly currentTime: number
  readonly destination: unknown
  readonly state: string
  createOscillator: () => OscillatorLike
  createGain: () => GainLike
  resume: () => Promise<void>
  close: () => Promise<void>
}

export interface OscillatorLike {
  frequency: { value: number }
  connect: (target: unknown) => void
  start: (when: number) => void
  stop: (when: number) => void
}

export interface GainLike {
  gain: { setValueAtTime: (value: number, when: number) => void; linearRampToValueAtTime: (value: number, when: number) => void }
  connect: (target: unknown) => void
}

interface Pattern {
  // Burst lengths and gaps in seconds, repeated every `period`.
  bursts: { at: number; length: number }[]
  period: number
}

const PATTERNS: Record<RingKind, Pattern> = {
  incoming: {
    bursts: [
      { at: 0, length: 0.4 },
      { at: 0.6, length: 0.4 },
    ],
    period: 3,
  },
  outgoing: { bursts: [{ at: 0, length: 1.2 }], period: 4 },
}

const TONES = [440, 480]
const VOLUME = 0.08

export function createRingtone(
  makeContext: () => AudioContextLike = () => new (window.AudioContext as unknown as new () => AudioContextLike)(),
  timers: { every: (run: () => void, ms: number) => () => void } = {
    every: (run, ms) => {
      const id = setInterval(run, ms)
      return () => clearInterval(id)
    },
  },
): Ringtone {
  let context: AudioContextLike | null = null
  let stopTimer: (() => void) | null = null

  function burst(audio: AudioContextLike, at: number, length: number) {
    const gain = audio.createGain()
    gain.gain.setValueAtTime(0, at)
    gain.gain.linearRampToValueAtTime(VOLUME, at + 0.02)
    gain.gain.setValueAtTime(VOLUME, at + length - 0.02)
    gain.gain.linearRampToValueAtTime(0, at + length)
    gain.connect(audio.destination)

    for (const frequency of TONES) {
      const tone = audio.createOscillator()
      tone.frequency.value = frequency
      tone.connect(gain)
      tone.start(at)
      tone.stop(at + length)
    }
  }

  function stop() {
    stopTimer?.()
    stopTimer = null

    const closing = context
    context = null
    closing?.close().catch(() => {})
  }

  return {
    start(kind) {
      stop()

      try {
        const audio = makeContext()
        context = audio
        audio.resume().catch(() => {})

        const pattern = PATTERNS[kind]
        const play = () => {
          const now = audio.currentTime + 0.05
          for (const { at, length } of pattern.bursts) burst(audio, now + at, length)
        }

        play()
        stopTimer = timers.every(play, pattern.period * 1000)
      } catch {
        // No sound is not a reason to stop the call.
        stop()
      }
    },
    stop,
  }
}
