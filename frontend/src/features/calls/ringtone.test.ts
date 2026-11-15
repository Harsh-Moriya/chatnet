import { describe, expect, it } from 'vitest'
import { createRingtone, type AudioContextLike, type GainLike, type OscillatorLike } from './ringtone'

class FakeContext implements AudioContextLike {
  currentTime = 10
  destination = {}
  state = 'running'
  oscillators: { frequency: number; start: number; stop: number }[] = []
  resumed = 0
  closed = false

  createOscillator(): OscillatorLike {
    const record = { frequency: 0, start: 0, stop: 0 }
    this.oscillators.push(record)
    return {
      frequency: {
        set value(v: number) {
          record.frequency = v
        },
        get value() {
          return record.frequency
        },
      },
      connect: () => {},
      start: (when) => void (record.start = when),
      stop: (when) => void (record.stop = when),
    }
  }

  createGain(): GainLike {
    return { gain: { setValueAtTime: () => {}, linearRampToValueAtTime: () => {} }, connect: () => {} }
  }

  async resume() {
    this.resumed++
  }

  async close() {
    this.closed = true
  }
}

function rig() {
  const contexts: FakeContext[] = []
  const timers: { run: () => void; ms: number; cleared: boolean }[] = []
  const ringtone = createRingtone(
    () => {
      const context = new FakeContext()
      contexts.push(context)
      return context
    },
    {
      every: (run, ms) => {
        const timer = { run, ms, cleared: false }
        timers.push(timer)
        return () => void (timer.cleared = true)
      },
    },
  )
  return { ringtone, contexts, timers }
}

describe('the ring', () => {
  it('plays two tones together, twice, for a call coming in, and again every three seconds', () => {
    const { ringtone, contexts, timers } = rig()

    ringtone.start('incoming')

    const [context] = contexts
    expect(context.resumed).toBe(1)
    expect(context.oscillators.map((o) => o.frequency)).toEqual([440, 480, 440, 480])
    expect(context.oscillators.map((o) => o.start)).toEqual([10.05, 10.05, 10.65, 10.65])
    expect(timers[0].ms).toBe(3000)

    timers[0].run()
    expect(context.oscillators).toHaveLength(8)
  })

  it('plays one long burst, every four seconds, for a call going out', () => {
    const { ringtone, contexts, timers } = rig()

    ringtone.start('outgoing')

    expect(contexts[0].oscillators).toHaveLength(2)
    expect(contexts[0].oscillators[0].stop - contexts[0].oscillators[0].start).toBeCloseTo(1.2)
    expect(timers[0].ms).toBe(4000)
  })

  it('stops by clearing its timer and closing its audio, and says nothing more', () => {
    const { ringtone, contexts, timers } = rig()
    ringtone.start('incoming')

    ringtone.stop()

    expect(timers[0].cleared).toBe(true)
    expect(contexts[0].closed).toBe(true)
    ringtone.stop()
  })

  it('stops what was ringing before it starts something else', () => {
    const { ringtone, contexts, timers } = rig()
    ringtone.start('incoming')

    ringtone.start('outgoing')

    expect(contexts[0].closed).toBe(true)
    expect(timers[0].cleared).toBe(true)
    expect(contexts).toHaveLength(2)
  })

  it('is silent, and no trouble, where there is no audio', () => {
    const ringtone = createRingtone(() => {
      throw new Error('no audio here')
    })

    expect(() => ringtone.start('incoming')).not.toThrow()
    expect(() => ringtone.stop()).not.toThrow()
  })
})
