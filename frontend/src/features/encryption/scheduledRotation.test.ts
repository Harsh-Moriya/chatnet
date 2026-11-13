import { describe, expect, it } from 'vitest'
import { ROTATION_INTERVAL_MS, ensureEpoch, keysAreDue, readyConversationWith } from './epochs'
import { type Person, newPerson, start } from './epochTestKit'
import { FakeEpochServer } from './fakeEpochServer'
import { underCurrentEpoch } from './sealedMessages'

// A conversation's key is replaced when it is a week old, by whoever opens or sends first, so one epoch key that leaks opens at most a
// week of messages. The browsers decide; the server only keeps its minimum of a day.

const DAY = 24 * 60 * 60 * 1000
const ISO = (ms: number) => new Date(ms).toISOString()

describe('when the keys of a conversation are due', () => {
  const now = Date.parse('2026-11-20T12:00:00Z')

  it('are not due when they are new', () => {
    expect(keysAreDue({ keyRotationRequired: false, keyEpochAt: ISO(now - DAY) }, now)).toBe(false)
  })

  it('are not due a moment before a week, and are due at a week', () => {
    expect(keysAreDue({ keyRotationRequired: false, keyEpochAt: ISO(now - ROTATION_INTERVAL_MS + 1) }, now)).toBe(false)
    expect(keysAreDue({ keyRotationRequired: false, keyEpochAt: ISO(now - ROTATION_INTERVAL_MS) }, now)).toBe(true)
  })

  it('a week is seven days', () => {
    expect(ROTATION_INTERVAL_MS).toBe(7 * DAY)
  })

  it('are due when someone left, however new they are', () => {
    expect(keysAreDue({ keyRotationRequired: true, keyEpochAt: ISO(now) }, now)).toBe(true)
    expect(keysAreDue({ keyRotationRequired: true }, now)).toBe(true)
  })

  it('are not due when nothing says when they were made, or what it says is not a time', () => {
    expect(keysAreDue({ keyRotationRequired: false }, now)).toBe(false)
    expect(keysAreDue({ keyRotationRequired: false, keyEpochAt: null }, now)).toBe(false)
    expect(keysAreDue({ keyRotationRequired: false, keyEpochAt: 'not a time' }, now)).toBe(false)
  })
})

async function pair() {
  const server = new FakeEpochServer()
  const [ana, ben] = [await newPerson(server), await newPerson(server)]
  const id = await start(server, ana, [ben])
  const clock = (person: Person): Person => ({ ...person, deps: { ...person.deps, now: () => server.now } })
  return { server, id, ana: clock(ana), ben: clock(ben) }
}

describe('making a conversation ready', () => {
  it('does nothing for keys that are six days old', async () => {
    const { server, id, ana } = await pair()
    server.advance(6 * DAY)
    server.calls.length = 0

    const result = await ensureEpoch(ana.deps, id)

    expect(result).toEqual({ epoch: 1, created: false })
  })

  it('replaces keys that are a week old, and the server takes it without asking for the time to pass', async () => {
    const { server, id, ana } = await pair()
    server.advance(ROTATION_INTERVAL_MS)

    const result = await ensureEpoch(ana.deps, id)

    expect(result).toEqual({ epoch: 2, created: true })
    expect(await ana.keys.load(ana.id, id, 2)).not.toBeNull()
  })

  it('makes the epoch for the people in the conversation as it is, a member who opens it later finds it and makes none', async () => {
    const { server, id, ana, ben } = await pair()
    server.advance(ROTATION_INTERVAL_MS)
    await ensureEpoch(ana.deps, id)

    const result = await ensureEpoch(ben.deps, id)

    expect(result).toEqual({ epoch: 2, created: false })
    expect(await ben.keys.load(ben.id, id, 2)).not.toBeNull()
  })

  it('starts the week again from the new epoch', async () => {
    const { server, id, ana } = await pair()
    server.advance(ROTATION_INTERVAL_MS)
    await ensureEpoch(ana.deps, id)
    server.advance(6 * DAY)

    expect((await ensureEpoch(ana.deps, id)).created).toBe(false)
  })

  it('asks the server nothing for keys that are held and not a week old, but does for keys that are', async () => {
    const { server, id, ana } = await pair()
    const known = { keyEpoch: 1, keyRotationRequired: false, keyEpochAt: ISO(server.now) }
    server.calls.length = 0

    await readyConversationWith(ana.deps, id, known)
    expect(server.calls).toEqual([])

    server.advance(ROTATION_INTERVAL_MS)
    await readyConversationWith(ana.deps, id, known)
    expect(server.calls).toContain('postEpoch')
  })

  it('does not treat a known state with no time as old', async () => {
    const { server, id, ana } = await pair()
    server.advance(30 * DAY)
    server.calls.length = 0

    await readyConversationWith(ana.deps, id, { keyEpoch: 1, keyRotationRequired: false })

    expect(server.calls).toEqual([])
  })
})

describe('sending when the keys are a week old', () => {
  it('replaces them first and seals under the new epoch', async () => {
    const { server, id, ana } = await pair()
    server.advance(ROTATION_INTERVAL_MS)
    const known = { keyEpoch: 1, keyRotationRequired: false, keyEpochAt: ISO(server.now - ROTATION_INTERVAL_MS) }

    const epoch = await underCurrentEpoch(ana.deps, id, known, async (e) => e)

    expect(epoch).toBe(2)
  })

  it('seals under the epoch it holds when the replacement could not be made, and does not refuse the message for it', async () => {
    const { server, id, ana } = await pair()
    server.advance(ROTATION_INTERVAL_MS)
    const known = { keyEpoch: 1, keyRotationRequired: false, keyEpochAt: ISO(server.now - ROTATION_INTERVAL_MS) }
    const refusing: Person = {
      ...ana,
      deps: {
        ...ana.deps,
        api: {
          ...ana.deps.api,
          postEpoch: async () => {
            throw new Error('offline')
          },
        },
      },
    }

    const epoch = await underCurrentEpoch(refusing.deps, id, known, async (e) => e)

    expect(epoch).toBe(1)
  })

  it('does not replace keys that are not a week old', async () => {
    const { server, id, ana } = await pair()
    server.advance(6 * DAY)
    const known = { keyEpoch: 1, keyRotationRequired: false, keyEpochAt: ISO(server.now - 6 * DAY) }
    server.calls.length = 0

    expect(await underCurrentEpoch(ana.deps, id, known, async (e) => e)).toBe(1)
    expect(server.calls).toEqual([])
  })

  it('still goes through the whole of making keys when someone left, which a replacement that cannot be made does not excuse', async () => {
    const { server, id, ana } = await pair()
    server.requireRotation(id)
    const refusing: Person = {
      ...ana,
      deps: { ...ana.deps, api: { ...ana.deps.api, postEpoch: async () => Promise.reject(new Error('offline')) } },
    }

    await expect(underCurrentEpoch(refusing.deps, id, { keyEpoch: 1, keyRotationRequired: true }, async (e) => e)).rejects.toThrow('offline')
  })
})
