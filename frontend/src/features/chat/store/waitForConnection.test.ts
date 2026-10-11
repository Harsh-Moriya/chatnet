import { beforeEach, describe, expect, it } from 'vitest'
import { useChatStore } from './chatStore'
import { waitForConnection } from './waitForConnection'

beforeEach(() => {
  useChatStore.getState().reset()
})

// A request that loads chat data must not start before the live connection is ready: a message sent
// between the load and the connection joining its groups would be lost. These tests are about that
// gate.
describe('waitForConnection', () => {
  it('returns at once when already connected', async () => {
    useChatStore.getState().setConnection('connected')

    await expect(waitForConnection()).resolves.toBeUndefined()
  })

  it('waits while the connection is not ready', async () => {
    let settled = false
    const waiting = waitForConnection().then(() => {
      settled = true
    })

    useChatStore.getState().setConnection('reconnecting')
    await Promise.resolve()
    expect(settled).toBe(false)

    useChatStore.getState().setConnection('connected')
    await waiting
    expect(settled).toBe(true)
  })

  it('releases every waiter when the connection comes up', async () => {
    const waiters = [waitForConnection(), waitForConnection(), waitForConnection()]

    useChatStore.getState().setConnection('connected')

    await expect(Promise.all(waiters)).resolves.toHaveLength(3)
  })

  it('stops waiting when the request is cancelled', async () => {
    const controller = new AbortController()
    const waiting = waitForConnection(controller.signal)

    controller.abort(new Error('cancelled'))

    await expect(waiting).rejects.toThrow('cancelled')
  })

  it('rejects at once when the signal was already cancelled', async () => {
    const controller = new AbortController()
    controller.abort(new Error('cancelled'))

    await expect(waitForConnection(controller.signal)).rejects.toThrow('cancelled')
  })

  it('stops listening once it has finished, so a later change does nothing', async () => {
    const controller = new AbortController()
    useChatStore.getState().setConnection('connected')
    await waitForConnection(controller.signal)

    controller.abort(new Error('too late'))

    // No error surfaces anywhere and a new wait still behaves normally.
    useChatStore.getState().setConnection('reconnecting')
    let settled = false
    const next = waitForConnection().then(() => {
      settled = true
    })
    await Promise.resolve()
    expect(settled).toBe(false)
    useChatStore.getState().setConnection('connected')
    await next
  })
})
