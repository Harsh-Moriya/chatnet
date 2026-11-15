import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fromHex } from '../../lib/crypto/bytes'
import { useAuthStore } from '../auth/store/authStore'
import { MissingKeysError } from '../encryption/epochs'
import { IdentityChangedError } from '../encryption/pins'
import { useEncryptionStore } from '../encryption/store'
import { createCallIdentity } from './callIdentity'
import { PeerIdentityChanged, PeerKeysUnavailable } from './peerIdentity'

// The adapter maps what the key code says about a person into what a call needs: their identity key, or a reason it cannot be had.

const resolvePeers = vi.hoisted(() => vi.fn())

vi.mock('../encryption/conversationKeys', () => ({ epochDeps: () => ({ marker: 'deps' }) }))
vi.mock('../encryption/epochs', async (importOriginal) => ({ ...(await importOriginal<typeof import('../encryption/epochs')>()), resolvePeers }))

const PEER = '9d8c7b6a-5f4e-4d3c-8b2a-1f0e9d8c7b6a'
const identityPublic = fromHex('ca4fa001829002d28aab4264da331987c50cd042de89728f9fde2b305eaf1b22')

beforeEach(() => {
  resolvePeers.mockReset()
})

describe('the other person’s identity key', () => {
  it('is the one in their checked bundle', async () => {
    resolvePeers.mockResolvedValue(new Map([[PEER, { identityPublic }]]))

    await expect(createCallIdentity().peerIdentity(PEER)).resolves.toBe(identityPublic)
    expect(resolvePeers).toHaveBeenCalledWith({ marker: 'deps' }, [PEER])
  })

  it('is not there for a person who has not set up encryption', async () => {
    resolvePeers.mockRejectedValue(new MissingKeysError([PEER]))

    await expect(createCallIdentity().peerIdentity(PEER)).rejects.toBeInstanceOf(PeerKeysUnavailable)
  })

  it('is not there when no bundle came back for them', async () => {
    resolvePeers.mockResolvedValue(new Map())

    await expect(createCallIdentity().peerIdentity(PEER)).rejects.toBeInstanceOf(PeerKeysUnavailable)
  })

  it('is refused when it is not the one this browser first saw', async () => {
    resolvePeers.mockRejectedValue(new IdentityChangedError(PEER, identityPublic))

    await expect(createCallIdentity().peerIdentity(PEER)).rejects.toBeInstanceOf(PeerIdentityChanged)
  })

  it('lets any other failure through, to be shown as a call that could not be made', async () => {
    resolvePeers.mockRejectedValue(new Error('Network down'))

    await expect(createCallIdentity().peerIdentity(PEER)).rejects.toThrow('Network down')
  })
})

describe('the keys of the signed-in person', () => {
  it('are the identity key in the open secrets, and the id of whoever is signed in', () => {
    const identity = { privateKey: new Uint8Array(32), publicKey: identityPublic }
    useAuthStore.setState({ user: { userId: 'me' } as never })
    useEncryptionStore.setState({ secrets: { identity, accounts: [] } as never })

    expect(createCallIdentity().selfId()).toBe('me')
    expect(createCallIdentity().signer()).toBe(identity)
  })

  it('are missing when nobody is signed in or the keys are not open', () => {
    useAuthStore.setState({ user: null })
    useEncryptionStore.setState({ secrets: null })

    expect(() => createCallIdentity().selfId()).toThrow('Nobody is signed in.')
    expect(() => createCallIdentity().signer()).toThrow('The keys are not open on this device.')
  })
})
