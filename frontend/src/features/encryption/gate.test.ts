import { describe, expect, it } from 'vitest'
import { gateScreen } from './gate'
import type { EncryptionStatus } from './store'

const everyStatus: EncryptionStatus[] = ['idle', 'loading', 'unavailable', 'error', 'notSetUp', 'locked', 'ready']

describe('what stands between signing in and the chat', () => {
  it('lets a browser that holds the keys through, whatever the state of checking them is', () => {
    for (const status of everyStatus) expect(gateScreen(status, true), status).toBe('chat')
  })

  it('waits while it has not yet found out', () => {
    expect(gateScreen('idle', false)).toBe('loading')
    expect(gateScreen('loading', false)).toBe('loading')
  })

  it('asks a person with no keys to set them up', () => {
    expect(gateScreen('notSetUp', false)).toBe('setUp')
  })

  it('asks a person whose keys are not on this browser to restore them', () => {
    expect(gateScreen('locked', false)).toBe('locked')
  })

  it('says so when this browser cannot keep keys, and when it could not find out', () => {
    expect(gateScreen('unavailable', false)).toBe('unavailable')
    expect(gateScreen('error', false)).toBe('error')
  })

  it('never lets a browser without keys through', () => {
    for (const status of everyStatus) expect(gateScreen(status, false), status).not.toBe('chat')
  })
})
