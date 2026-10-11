import { describe, expect, it } from 'vitest'
import { reconnectDelay } from './signalr'

// The chat should come back on its own after the network drops or the server restarts, and should
// not hammer a server that is struggling. The delays grow, then stay at a ceiling for ever: the
// client never gives up, because a laptop closed overnight should reconnect when it is opened.
describe('reconnectDelay', () => {
  it('tries again at once, then waits longer each time', () => {
    expect([0, 1, 2, 3].map(reconnectDelay)).toEqual([0, 2000, 5000, 10000])
  })

  it('settles at 30 seconds and stays there', () => {
    expect(reconnectDelay(4)).toBe(30000)
    expect(reconnectDelay(5)).toBe(30000)
    expect(reconnectDelay(10_000)).toBe(30000)
  })
})
