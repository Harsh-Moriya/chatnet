import { fromBase64Url, toBase64Url } from '../../lib/crypto/bytes'
import { SIGNATURE_BYTES } from '../../lib/crypto/formats'

// What goes through the server as the payload of a call signal, which the server relays as text and reads none of (at most 16 KiB). An offer
// or an answer carries the session description and the signature of its fingerprints (see lib/crypto/call.ts); a candidate carries one
// ICE candidate. Decoding never throws: what does not look right is null, and the controller ends the call.

export interface SdpSignal {
  sdp: string
  signature: Uint8Array
}

export interface CandidateInit {
  candidate: string
  sdpMid: string | null
  sdpMLineIndex: number | null
  usernameFragment: string | null
}

// A session description with audio and video is a few thousand characters; this is well under the server's limit once the JSON is added.
export const MAX_SDP_LENGTH = 12000
export const MAX_CANDIDATE_LENGTH = 2000

export function encodeSdpSignal(sdp: string, signature: Uint8Array): string {
  return JSON.stringify({ sdp, signature: toBase64Url(signature) })
}

export function decodeSdpSignal(payload: string): SdpSignal | null {
  const fields = parse(payload)
  if (fields === null) return null

  const { sdp, signature } = fields
  if (typeof sdp !== 'string' || sdp.length === 0 || sdp.length > MAX_SDP_LENGTH) return null
  if (typeof signature !== 'string') return null

  try {
    const bytes = fromBase64Url(signature)
    return bytes.length === SIGNATURE_BYTES ? { sdp, signature: bytes } : null
  } catch {
    return null
  }
}

export function encodeCandidate(candidate: CandidateInit): string {
  return JSON.stringify({
    candidate: candidate.candidate,
    sdpMid: candidate.sdpMid,
    sdpMLineIndex: candidate.sdpMLineIndex,
    usernameFragment: candidate.usernameFragment,
  })
}

export function decodeCandidate(payload: string): CandidateInit | null {
  const fields = parse(payload)
  if (fields === null) return null

  const { candidate, sdpMid, sdpMLineIndex, usernameFragment } = fields
  if (typeof candidate !== 'string' || candidate.length > MAX_CANDIDATE_LENGTH) return null
  if (sdpMid !== null && sdpMid !== undefined && typeof sdpMid !== 'string') return null
  if (usernameFragment !== null && usernameFragment !== undefined && typeof usernameFragment !== 'string') return null
  if (sdpMLineIndex !== null && sdpMLineIndex !== undefined && (typeof sdpMLineIndex !== 'number' || !Number.isInteger(sdpMLineIndex) || sdpMLineIndex < 0)) {
    return null
  }

  return {
    candidate,
    sdpMid: (sdpMid as string | null | undefined) ?? null,
    sdpMLineIndex: (sdpMLineIndex as number | null | undefined) ?? null,
    usernameFragment: (usernameFragment as string | null | undefined) ?? null,
  }
}

function parse(payload: string): Record<string, unknown> | null {
  try {
    const value: unknown = JSON.parse(payload)
    return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : null
  } catch {
    return null
  }
}
