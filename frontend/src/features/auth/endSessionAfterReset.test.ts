import { describe, expect, it, vi } from 'vitest'
import { endSessionAfterReset } from './endSessionAfterReset'
import type { RefreshResult, SessionStatus } from './sessionManager'

function setup(status: SessionStatus, refreshed: RefreshResult) {
  const refresh = vi.fn(async () => refreshed)
  const signOut = vi.fn(async () => {})
  return { refresh, signOut, run: () => endSessionAfterReset({ status: () => status, refresh, signOut }) }
}

// The browser may belong to the person whose password was reset, to someone else, or to nobody. Asking the server to renew the
// session tells the first from the second: it refuses only for the first.
describe('ending the session of this browser after a password reset', () => {
  it('signs out when the server refuses to renew the session: it was the reset account', async () => {
    const { run, refresh, signOut } = setup('authenticated', 'ended')

    await run()

    expect(refresh).toHaveBeenCalledOnce()
    expect(signOut).toHaveBeenCalledOnce()
  })

  it('keeps the person signed in when the session renews: the browser belongs to someone else', async () => {
    const { run, signOut } = setup('authenticated', 'renewed')

    await run()

    expect(signOut).not.toHaveBeenCalled()
  })

  it('does not sign out when the server could not be reached: nothing was learned', async () => {
    const { run, signOut } = setup('authenticated', 'unavailable')

    await run()

    expect(signOut).not.toHaveBeenCalled()
  })

  it('does nothing at all when nobody is signed in', async () => {
    const anonymous = setup('anonymous', 'ended')
    const unknown = setup('unknown', 'ended')

    await anonymous.run()
    await unknown.run()

    expect(anonymous.refresh).not.toHaveBeenCalled()
    expect(anonymous.signOut).not.toHaveBeenCalled()
    expect(unknown.refresh).not.toHaveBeenCalled()
    expect(unknown.signOut).not.toHaveBeenCalled()
  })

  it('signs out only after the renewal has been answered', async () => {
    const order: string[] = []
    await endSessionAfterReset({
      status: () => 'authenticated',
      refresh: async () => {
        await Promise.resolve()
        order.push('refresh')
        return 'ended'
      },
      signOut: async () => {
        order.push('signOut')
      },
    })

    expect(order).toEqual(['refresh', 'signOut'])
  })
})
