import { beforeEach, describe, expect, it } from 'vitest'
import { createAccountSecrets, rotateAccountKey } from '../../lib/crypto'
import { useAuthStore } from '../auth/store/authStore'
import { messageOpener } from './conversationKeys'
import { useEncryptionStore } from './store'

// The opener keeps what it learned for as long as the same person is signed in with the same keys, and not longer.

const ANA = '3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f'
const BEN = '9d8c7b6a-5e4f-4a3b-8c2d-1e0f9a8b7c6d'

function signInWith(userId: string, secrets: Awaited<ReturnType<typeof createAccountSecrets>>) {
  useAuthStore.setState({ user: { userId } as never })
  useEncryptionStore.getState().applyReady({ deviceId: 'device', secrets })
}

beforeEach(() => {
  useAuthStore.setState({ user: null })
  useEncryptionStore.getState().reset()
})

describe('the opener of messages', () => {
  it('is the same one while the same person holds the same keys, so what it learned is kept', async () => {
    signInWith(ANA, await createAccountSecrets())

    expect(messageOpener()).toBe(messageOpener())
  })

  it('is a new one when the person got a new generation of their keys, which the old one would open nothing with', async () => {
    const first = await createAccountSecrets()
    signInWith(ANA, first)
    const before = messageOpener()

    signInWith(ANA, await rotateAccountKey(first))

    expect(messageOpener()).not.toBe(before)
  })

  it('is a new one when the person started over with keys that have nothing of the old ones', async () => {
    signInWith(ANA, await createAccountSecrets())
    const before = messageOpener()

    signInWith(ANA, await createAccountSecrets())

    expect(messageOpener()).not.toBe(before)
  })

  it('is a new one for another person', async () => {
    const secrets = await createAccountSecrets()
    signInWith(ANA, secrets)
    const before = messageOpener()

    signInWith(BEN, secrets)

    expect(messageOpener()).not.toBe(before)
  })
})
