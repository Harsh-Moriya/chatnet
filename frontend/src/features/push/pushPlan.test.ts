import { describe, expect, it } from 'vitest'
import { type PushFacts, planSync, switchState } from './pushPlan'

const facts = (overrides: Partial<PushFacts> = {}): PushFacts => ({
  supported: true,
  serverHasPush: true,
  permission: 'granted',
  wantsAlerts: true,
  subscription: 'current',
  ...overrides,
})

describe('what the page does when it loads', () => {
  it('registers a browser that is subscribed with the current key for a person who wants alerts', () => {
    expect(planSync(facts())).toBe('register')
  })

  it('subscribes a browser that has no subscription yet', () => {
    expect(planSync(facts({ subscription: 'none' }))).toBe('subscribe')
  })

  it('replaces a subscription made with a key the server no longer has', () => {
    expect(planSync(facts({ subscription: 'stale' }))).toBe('replace')
  })

  it('does nothing in a browser that cannot do it, or against a server that has no keys', () => {
    expect(planSync(facts({ supported: false }))).toBe('nothing')
    expect(planSync(facts({ serverHasPush: false }))).toBe('nothing')
    expect(planSync(facts({ supported: false, wantsAlerts: false, subscription: 'current' }))).toBe('nothing')
    expect(planSync(facts({ serverHasPush: false, wantsAlerts: false, subscription: 'current' }))).toBe('nothing')
  })

  it('does nothing for a person who did not ask, when there is nothing to undo', () => {
    expect(planSync(facts({ wantsAlerts: false, subscription: 'none' }))).toBe('nothing')
  })

  // A browser is shared by whoever signs in on it. Alerts for the person who asked must not appear on the screen of the next one.
  it('drops a subscription that this person did not ask for', () => {
    expect(planSync(facts({ wantsAlerts: false, subscription: 'current' }))).toBe('drop')
    expect(planSync(facts({ wantsAlerts: false, subscription: 'stale' }))).toBe('drop')
  })

  it('drops it whatever the permission is, because it is about who the alerts are for', () => {
    expect(planSync(facts({ wantsAlerts: false, subscription: 'current', permission: 'denied' }))).toBe('drop')
    expect(planSync(facts({ wantsAlerts: false, subscription: 'current', permission: 'default' }))).toBe('drop')
  })

  it('never subscribes without permission, because there is no click to ask with', () => {
    expect(planSync(facts({ permission: 'default', subscription: 'none' }))).toBe('nothing')
    expect(planSync(facts({ permission: 'denied', subscription: 'none' }))).toBe('nothing')
    expect(planSync(facts({ permission: 'denied', subscription: 'current' }))).toBe('nothing')
  })
})

describe('what the switch shows', () => {
  it('is on for a person who asked, in a browser that is subscribed with the current key', () => {
    expect(switchState(facts())).toBe('on')
  })

  it('is off for a person who did not ask, or whose browser is not subscribed yet, or was subscribed with an old key', () => {
    expect(switchState(facts({ wantsAlerts: false }))).toBe('off')
    expect(switchState(facts({ subscription: 'none' }))).toBe('off')
    expect(switchState(facts({ subscription: 'stale' }))).toBe('off')
    expect(switchState(facts({ permission: 'default' }))).toBe('off')
  })

  it('is blocked when the browser says no, whatever else is true', () => {
    expect(switchState(facts({ permission: 'denied' }))).toBe('blocked')
    expect(switchState(facts({ permission: 'denied', wantsAlerts: false, subscription: 'none' }))).toBe('blocked')
  })

  it('says a browser cannot do it before it says a server has no keys', () => {
    expect(switchState(facts({ supported: false, serverHasPush: false }))).toBe('unsupported')
    expect(switchState(facts({ serverHasPush: false }))).toBe('unavailable')
  })
})
