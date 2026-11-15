import type { KeyPair } from '../../lib/crypto/primitives'

// What a call needs to know about who is who: the signed-in person's identity key, and the other person's identity key as this browser holds
// it (pinned when it first saw it). Behind an interface so the controller does not depend on how keys are stored.

// The other person has not set up encryption, so there is nothing to check a call against.
export class PeerKeysUnavailable extends Error {
  constructor() {
    super('The other person has not set up encryption.')
    this.name = 'PeerKeysUnavailable'
  }
}

// The other person's identity key is not the one this browser first saw for them: the safety number has to be compared before it is
// trusted again.
export class PeerIdentityChanged extends Error {
  constructor() {
    super('The identity key of the other person changed.')
    this.name = 'PeerIdentityChanged'
  }
}

export interface CallIdentity {
  selfId: () => string
  signer: () => KeyPair
  // The other person's identity public key, checked. Rejects with PeerKeysUnavailable or PeerIdentityChanged.
  peerIdentity: (peerId: string) => Promise<Uint8Array>
}
