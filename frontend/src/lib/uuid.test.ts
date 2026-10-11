import { afterEach, describe, expect, it, vi } from 'vitest'
import { newClientMessageId } from './uuid'

const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('newClientMessageId', () => {
  it('makes a version 4 UUID', () => {
    expect(newClientMessageId()).toMatch(UUID_V4)
  })

  it('does not repeat', () => {
    const ids = new Set(Array.from({ length: 2000 }, () => newClientMessageId()))

    expect(ids.size).toBe(2000)
  })

  // crypto.randomUUID exists only in secure contexts (https or localhost). Opening the dev server
  // from a phone on the local network over plain http is neither, and sending a message must still
  // work there.
  it('still works where crypto.randomUUID is unavailable', () => {
    vi.stubGlobal('crypto', { getRandomValues: crypto.getRandomValues.bind(crypto) })

    const ids = Array.from({ length: 500 }, () => newClientMessageId())

    expect(ids.every((id) => UUID_V4.test(id))).toBe(true)
    expect(new Set(ids).size).toBe(500)
  })
})
