import { beforeEach, describe, expect, it } from 'vitest'
import { createAccountSecrets } from '../../lib/crypto'
import { useEncryptionStore } from './store'

beforeEach(() => {
  useEncryptionStore.getState().reset()
})

const state = () => useEncryptionStore.getState()

describe('what the screens know about the keys', () => {
  it('starts knowing nothing', () => {
    expect(state()).toMatchObject({ status: 'idle', deviceId: null, secrets: null, removedElsewhere: false })
  })

  it('reads a ready answer as ready, with the device and the keys', async () => {
    const secrets = await createAccountSecrets()

    state().applyLoaded({ state: { phase: 'ready', deviceId: 'd1', secrets }, removedElsewhere: false })

    expect(state()).toMatchObject({ status: 'ready', deviceId: 'd1', secrets, removedElsewhere: false })
  })

  it.each(['notSetUp', 'locked'] as const)('reads %s as that, with no device and no keys', async (phase) => {
    const secrets = await createAccountSecrets()
    state().applyReady({ deviceId: 'd1', secrets })

    state().applyLoaded({ state: { phase }, removedElsewhere: false })

    expect(state()).toMatchObject({ status: phase, deviceId: null, secrets: null })
  })

  it('remembers that this browser was removed, until it is dismissed', () => {
    state().applyLoaded({ state: { phase: 'locked' }, removedElsewhere: true })
    expect(state().removedElsewhere).toBe(true)

    state().acknowledgeRemoval()

    expect(state().removedElsewhere).toBe(false)
  })

  it('stops saying it was removed once it has keys again', async () => {
    state().applyLoaded({ state: { phase: 'locked' }, removedElsewhere: true })

    state().applyReady({ deviceId: 'd2', secrets: await createAccountSecrets() })

    expect(state()).toMatchObject({ status: 'ready', deviceId: 'd2', removedElsewhere: false })
  })

  it('forgets everything when the person signs out, keys included', async () => {
    state().applyReady({ deviceId: 'd1', secrets: await createAccountSecrets() })

    state().reset()

    expect(state()).toMatchObject({ status: 'idle', deviceId: null, secrets: null, removedElsewhere: false })
  })

  it('shows what is going on while it asks', () => {
    state().setStatus('loading')

    expect(state().status).toBe('loading')
  })
})
