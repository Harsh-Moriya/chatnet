import { beforeEach, describe, expect, it } from 'vitest'
import { shownAsVerified, useIdentityChanges } from './identityChanges'

beforeEach(() => {
  useIdentityChanges.getState().reset()
})

const state = () => useIdentityChanges.getState()

describe('the mark of a verified person', () => {
  it('is shown for a person who was verified and whose key did not change', () => {
    expect(shownAsVerified(true, {}, 'ben')).toBe(true)
  })

  it('is not shown for a person who was not verified', () => {
    expect(shownAsVerified(false, {}, 'ben')).toBe(false)
  })

  it('is not shown while the key of the person has changed and the change is not accepted', () => {
    expect(shownAsVerified(true, { ben: Uint8Array.of(1) }, 'ben')).toBe(false)
    expect(shownAsVerified(true, { cy: Uint8Array.of(1) }, 'ben')).toBe(true)
  })

  it('is not shown for nobody', () => {
    expect(shownAsVerified(true, {}, null)).toBe(false)
  })
})

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
