import { useAuthStore } from '../auth/store/authStore'
import { useEncryptionStore } from '../encryption/store'
import { PeerIdentityChanged, PeerKeysUnavailable, type CallIdentity } from './peerIdentity'

// The keys a call is signed and checked with, from where the application holds them: the signed-in person's identity key, and the other
// person's identity key as `resolvePeers` gives it (checked against their signed bundle, and the one this browser first saw for them). The
// key code is loaded when the first call needs it, as it is for the first message, and not with the page.
export function createCallIdentity(): CallIdentity {
  return {
    selfId() {
      const userId = useAuthStore.getState().user?.userId
      if (userId === undefined) throw new Error('Nobody is signed in.')
      return userId
    },

    signer() {
      const secrets = useEncryptionStore.getState().secrets
      if (secrets === null) throw new Error('The keys are not open on this device.')
      return secrets.identity
    },

    async peerIdentity(peerId) {
      const [{ epochDeps }, { MissingKeysError, resolvePeers }, { IdentityChangedError }] = await Promise.all([
        import('../encryption/conversationKeys'),
        import('../encryption/epochs'),
        import('../encryption/pins'),
      ])

      try {
        const bundles = await resolvePeers(epochDeps(), [peerId])
        const bundle = bundles.get(peerId)
        if (bundle === undefined) throw new PeerKeysUnavailable()
        return bundle.identityPublic
      } catch (failure) {
        if (failure instanceof MissingKeysError) throw new PeerKeysUnavailable()
        if (failure instanceof IdentityChangedError) throw new PeerIdentityChanged()
        throw failure
      }
    },
  }
}
