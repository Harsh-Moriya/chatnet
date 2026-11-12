import { CryptoError, fromBase64Url, openMessage, readMessageEpoch, sealMessage, toBase64Url } from '../../lib/crypto'
import { type EpochDeps, type KnownKeyState, MissingKeysError, ensureEpoch, loadEpochKeys, resolvePeers, withEpochRetries } from './epochs'
import { IdentityChangedError } from './pins'

// Messages: sealing the text a person writes and opening what others wrote. A message is one blob that the server stores and hands
// on without being able to read it; the key is the one of the conversation's epoch. Nothing here touches the network, IndexedDB or the
// page directly, so a test drives the real cryptography against a fake server.

// ##### What the server hands out #####

// What this module needs of a message, whatever a screen calls its fields. `ciphertext` is the blob as unpadded base64url.
export interface SealedMessage {
  conversationId: string
  senderId: string
  clientMessageId: string
  // How many times the sender had edited the message when it was sealed. It is part of what the signature covers.
  editCount: number
  keyEpoch: number
  ciphertext: string
}

// Why a message cannot be shown. A message that cannot be shown is a state of the message, not an error: the rest of the
// conversation is read as usual.
export type UnreadableReason =
  // This browser holds no key for the epoch the message was sealed in, and the server has none for this person to give.
  | 'noKey'
  // The blob is not a message of this conversation by this sender at this edit: it is malformed, was moved, or does not check out.
  | 'invalid'
  // The sender has no keys on the server, so there is nothing to check the signature against.
  | 'senderUnknown'
  // The sender's identity key is not the one this browser first saw for them.
  | 'senderChanged'

export type OpenedMessage = { readable: true; text: string } | { readable: false; reason: UnreadableReason }

const unreadable = (reason: UnreadableReason): OpenedMessage => ({ readable: false, reason })

// ##### Sealing #####

// The key of an epoch is not held by this browser, and the server could not give it either.
export class EpochKeyMissingError extends Error {
  constructor(epoch: number) {
    super(`The key of epoch ${epoch} is not available on this browser.`)
    this.name = 'EpochKeyMissingError'
  }
}

// The key of an epoch: held here, or opened from the person's envelope for it when the server has one. Null when neither.
async function heldKey(deps: EpochDeps, conversationId: string, epoch: number): Promise<Uint8Array | null> {
  const held = await deps.keys.load(deps.self.userId, conversationId, epoch)
  if (held) return held

  await loadEpochKeys(deps, conversationId, { from: epoch })
  return deps.keys.load(deps.self.userId, conversationId, epoch)
}

export interface SealInput {
  conversationId: string
  clientMessageId: string
  editCount: number
  epoch: number
  text: string
}

export interface Sealed {
  ciphertext: string
  keyEpoch: number
}

// The text as a blob sealed under the key of the epoch and signed by this person's identity key. The files of a message are not part
// of it yet, so the payload names none.
export async function sealText(deps: EpochDeps, input: SealInput): Promise<Sealed> {
  const epochKey = await heldKey(deps, input.conversationId, input.epoch)
  if (!epochKey) throw new EpochKeyMissingError(input.epoch)

  const blob = await sealMessage({
    context: {
      conversationId: input.conversationId,
      senderId: deps.self.userId,
      clientMessageId: input.clientMessageId,
      editCount: input.editCount,
    },
    epoch: input.epoch,
    epochKey,
    payload: { text: input.text, files: [] },
    signer: deps.self.secrets.identity,
  })

  return { ciphertext: toBase64Url(blob), keyEpoch: input.epoch }
}

// Runs `work` with an epoch it can seal under: the one the conversation is in, ready to use here. The first try goes by what the screen
// knows already, so a conversation whose key is held costs the server nothing; after a refusal (another epoch was stored, or a removal
// marked this one for replacement) the state is asked for again, and the epoch is adopted or made. The work is run again, up to
// MAX_ATTEMPTS times, while the server says the epoch was the wrong one.
export async function underCurrentEpoch<T>(
  deps: EpochDeps,
  conversationId: string,
  known: KnownKeyState,
  work: (epoch: number) => Promise<T>,
): Promise<T> {
  let firstAttempt = true

  return withEpochRetries(async () => {
    const usable =
      firstAttempt &&
      known.keyEpoch >= 1 &&
      !known.keyRotationRequired &&
      (await deps.keys.load(deps.self.userId, conversationId, known.keyEpoch)) !== null
    firstAttempt = false

    const epoch = usable ? known.keyEpoch : (await ensureEpoch(deps, conversationId)).epoch
    return work(epoch)
  })
}

// ##### Opening #####

export interface MessageOpener {
  // Never throws: a message that cannot be opened is reported with the reason.
  open(message: SealedMessage): Promise<OpenedMessage>
  // Forgets that keys were missing for a conversation, so the next message of it asks the server again. For when a new epoch is
  // announced, or the person has just restored their keys.
  forget(conversationId: string): void
}

// An opener keeps what it learned, so a page of messages costs one fetch of the conversation's keys and one lookup of each sender, not
// one for each message: the fetches in progress, the epochs the server had no key for, and the identity key of each sender.
export function createMessageOpener(deps: EpochDeps): MessageOpener {
  const self = deps.self.userId
  const loading = new Map<string, Promise<void>>()
  const missing = new Set<string>()
  const senders = new Map<string, Promise<Uint8Array | UnreadableReason>>()

  // The key of an epoch, asked of the server once if it is not held and not asked for before. A failure to reach the server is not
  // remembered, because the key may be there next time.
  async function keyFor(conversationId: string, epoch: number): Promise<Uint8Array | null> {
    const held = await deps.keys.load(self, conversationId, epoch)
    if (held) return held

    const marker = `${conversationId}:${epoch}`
    if (missing.has(marker)) return null

    let inFlight = loading.get(marker)
    if (!inFlight) {
      inFlight = loadEpochKeys(deps, conversationId, { from: epoch })
        .then(() => undefined)
        .finally(() => loading.delete(marker))
      loading.set(marker, inFlight)
    }

    try {
      await inFlight
    } catch {
      return null
    }

    const loaded = await deps.keys.load(self, conversationId, epoch)
    if (!loaded) missing.add(marker)
    return loaded
  }

  // The identity key to check a sender's signature against. Their own messages are checked against this browser's own key.
  function identityOf(senderId: string): Promise<Uint8Array | UnreadableReason> {
    if (senderId === self) return Promise.resolve(deps.self.secrets.identity.publicKey)

    const cached = senders.get(senderId)
    if (cached) return cached

    const looked = resolvePeers(deps, [senderId])
      .then((bundles) => bundles.get(senderId)!.identityPublic)
      .catch((failure: unknown): UnreadableReason => {
        if (failure instanceof IdentityChangedError) return 'senderChanged'
        if (failure instanceof MissingKeysError || failure instanceof CryptoError) return 'senderUnknown'
        senders.delete(senderId)
        return 'senderUnknown'
      })

    senders.set(senderId, looked)
    return looked
  }

  return {
    async open(message) {
      try {
        const blob = fromBase64Url(message.ciphertext)
        if (readMessageEpoch(blob) !== message.keyEpoch) return unreadable('invalid')

        const epochKey = await keyFor(message.conversationId, message.keyEpoch)
        if (!epochKey) return unreadable('noKey')

        const signer = await identityOf(message.senderId)
        if (typeof signer === 'string') return unreadable(signer)

        const payload = await openMessage({
          context: {
            conversationId: message.conversationId,
            senderId: message.senderId,
            clientMessageId: message.clientMessageId,
            editCount: message.editCount,
          },
          epochKey,
          signerPublic: signer,
          blob,
        })

        return { readable: true, text: payload.text }
      } catch (failure) {
        // A blob that does not check out is wrong; anything else (the network, the key store) may be right the next time.
        return unreadable(failure instanceof CryptoError ? 'invalid' : 'noKey')
      }
    },

    forget(conversationId) {
      for (const marker of [...missing]) if (marker.startsWith(`${conversationId}:`)) missing.delete(marker)
    },
  }
}
