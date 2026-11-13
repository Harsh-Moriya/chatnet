import { beforeEach, describe, expect, it } from 'vitest'
import { useIdentityChanges } from './identityChanges'

beforeEach(() => {
  useIdentityChanges.getState().reset()
})

const state = () => useIdentityChanges.getState()

describe('the people whose key changed', () => {
  it('starts with nobody', () => {
    expect(state().changed).toEqual({})
  })

  it('remembers the key just seen for a person, and the newest if it changes again', () => {
    state().note('ben', Uint8Array.of(1))
    state().note('ben', Uint8Array.of(2))

    expect(state().changed).toEqual({ ben: Uint8Array.of(2) })
  })

  it('keeps people apart', () => {
    state().note('ben', Uint8Array.of(1))
    state().note('cy', Uint8Array.of(2))

    state().clear('ben')

    expect(Object.keys(state().changed)).toEqual(['cy'])
  })

  it('changes nothing when a person who did not change is cleared', () => {
    state().note('ben', Uint8Array.of(1))
    const before = state().changed

    state().clear('nobody')

    expect(state().changed).toBe(before)
  })

  it('counts every time the marks were set or taken away, so what shows them reads them again', () => {
    state().markedAgain()
    state().markedAgain()

    expect(state().marks).toBe(2)
  })

  it('forgets everything, as when the person signs out', () => {
    state().note('ben', Uint8Array.of(1))
    state().markedAgain()

    state().reset()

    expect(state()).toMatchObject({ changed: {}, marks: 0 })
  })
})
