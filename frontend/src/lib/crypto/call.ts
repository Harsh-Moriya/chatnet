// What makes a call's media safe from the server. WebRTC encrypts audio and video with DTLS-SRTP, and each side names the certificate it
// will use in its session description (SDP) as `a=fingerprint:<algorithm> <hex>`. The server relays the SDP, so it could replace the
// fingerprints with its own and sit in the middle of the call. Each side therefore signs the fingerprints of its SDP with its identity key
// (the one its safety number is made of) and the other side checks them against the identity it holds for that person: an SDP whose
// fingerprints were changed on the way no longer matches its signature.
//
// The signed input is, after the label and a zero byte (see formats.ts):
//
//   callId(16) | conversationId(16) | signerId(16) | peerId(16) | role(1) | count(2) | for each fingerprint: length(2) | UTF-8
//
// with role 0 for an offer and 1 for an answer. The ids bind the signature to this call, this conversation and this direction, so it cannot
// be replayed into another call or turned around into the other side's answer. The fingerprints are those of the SDP in the order that does
// not depend on how the browser wrote it: normalised (lower-case algorithm, upper-case hex), without repeats, sorted. Candidates are not
// signed: a false one can only make the connection fail, because the other side cannot complete the DTLS handshake without the certificate
// the signed fingerprint names.

import { CryptoError, u16, utf8, uuidBytes, type Bytes } from './bytes'
import { LABELS, SIGNATURE_BYTES, labelled } from './formats'
import { ed25519Sign, ed25519Verify, type KeyPair } from './primitives'

export type CallRole = 'offer' | 'answer'

export interface CallContext {
  callId: string
  conversationId: string
  // Whoever signs, and the other person.
  signerId: string
  peerId: string
}

// Browsers write one per media section, all of the one certificate, so a real SDP has one or two after repeats are removed. More than this
// is not a call.
export const MAX_FINGERPRINTS = 8

const ROLE_BYTE: Record<CallRole, number> = { offer: 0, answer: 1 }

const FINGERPRINT_LINE = /^a=fingerprint:([A-Za-z0-9-]+) ([0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2})*)\s*$/

// The fingerprints an SDP names, normalised and sorted. An SDP that names none, or has a fingerprint line that is not one, is refused: nothing
// would then be signed or checked.
export function fingerprintsOf(sdp: string): string[] {
  const found = new Set<string>()

  for (const line of sdp.split(/\r\n|\n/)) {
    if (!line.startsWith('a=fingerprint:')) continue

    const match = FINGERPRINT_LINE.exec(line)
    if (match === null) throw new CryptoError('malformed', 'A fingerprint line of the session description is not valid')
    found.add(`${match[1].toLowerCase()} ${match[2].toUpperCase()}`)
  }

  if (found.size === 0) throw new CryptoError('malformed', 'The session description names no fingerprint')
  if (found.size > MAX_FINGERPRINTS) throw new CryptoError('malformed', 'The session description names too many fingerprints')

  return [...found].sort()
}

export function callSignatureInput(context: CallContext, role: CallRole, fingerprints: readonly string[]): Bytes {
  const encoded = fingerprints.map((fingerprint) => utf8(fingerprint))

  return labelled(
    LABELS.callSignature,
    uuidBytes(context.callId),
    uuidBytes(context.conversationId),
    uuidBytes(context.signerId),
    uuidBytes(context.peerId),
    new Uint8Array([ROLE_BYTE[role]]),
    u16(encoded.length),
    ...encoded.flatMap((bytes) => [u16(bytes.length), bytes]),
  )
}

// The signature of this side over the fingerprints of the SDP it is about to send.
export async function signCall(signer: KeyPair, context: CallContext, role: CallRole, sdp: string): Promise<Bytes> {
  return ed25519Sign(signer.privateKey, callSignatureInput(context, role, fingerprintsOf(sdp)))
}

// Whether the signature is the signer's over the fingerprints of the SDP that arrived. False for any signature that does not match, including
// one of the wrong length; a malformed SDP throws, which a caller treats as a failed verification too.
export async function verifyCall(
  signerPublic: Uint8Array,
  context: CallContext,
  role: CallRole,
  sdp: string,
  signature: Uint8Array,
): Promise<boolean> {
  if (signature.length !== SIGNATURE_BYTES) return false

  return ed25519Verify(signerPublic, callSignatureInput(context, role, fingerprintsOf(sdp)), signature)
}
