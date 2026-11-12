// A message as the server stores it: one opaque blob.
//
//   version(1) | suite(1) | epoch(4) | nonce(12) | ciphertext | tag(16) | signature(64)
//
// The ciphertext holds the padded payload (text and the files of the message, each with its key).
// The AES-GCM associated data and the Ed25519 signature both cover the conversation, the sender,
// the id the client gave the message and the edit count, so the server cannot move a message to
// another conversation, show it as sent by someone else, or replay an older version as a newer
// edit. The sender signs after encrypting, and readers verify before decrypting.

import { CryptoError, concat, fromBase64Url, fromUtf8, readU32, u32, utf8, uuidBytes, type Bytes } from './bytes'
import { MAX_CHUNK_BYTES } from './file'
import { KEY_BYTES, LABELS, NONCE_BYTES, SIGNATURE_BYTES, SUITE_V1, TAG_BYTES, VERSION, labelled } from './formats'
import { aesGcmDecrypt, aesGcmEncrypt, ed25519Sign, ed25519Verify, randomBytes, type KeyPair } from './primitives'

export const PADDING_BLOCK = 256
export const MAX_MESSAGE_BLOB_BYTES = 32 * 1024

// The longest name a file entry may have, in UTF-16 code units. Four of them and the text of a message have to fit in a blob.
export const MAX_FILE_NAME_LENGTH = 255

const HEADER_BYTES = 2 + 4 + NONCE_BYTES
const MIN_BLOB_BYTES = HEADER_BYTES + TAG_BYTES + SIGNATURE_BYTES

export interface MessageContext {
  conversationId: string
  senderId: string
  clientMessageId: string
  editCount: number
}

// A file of the message. The key and the layout are inside the encrypted payload; the bytes are
// in the bucket as an encrypted file (see file.ts), in the same order as the attachments.
export interface FileRef {
  name: string
  type: string
  size: number
  // base64url of the 32-byte file key.
  key: string
  chunkSize: number
}

export interface MessagePayload {
  text: string
  files: FileRef[]
}

function contextBytes(context: MessageContext): Bytes {
  return concat(uuidBytes(context.conversationId), uuidBytes(context.senderId), uuidBytes(context.clientMessageId), u32(context.editCount))
}

// Padding hides the length of short messages: a delimiter byte 0x80 and zeros up to a multiple of
// 256 bytes.
export function pad(plain: Uint8Array): Bytes {
  const padded = new Uint8Array(Math.floor(plain.length / PADDING_BLOCK + 1) * PADDING_BLOCK)
  padded.set(plain)
  padded[plain.length] = 0x80
  return padded
}

export function unpad(padded: Uint8Array): Bytes {
  let end = padded.length
  while (end > 0 && padded[end - 1] === 0) end--
  if (end === 0 || padded[end - 1] !== 0x80) throw new CryptoError('malformed', 'Padding is not valid')
  return new Uint8Array(padded.subarray(0, end - 1))
}

// The compact form, keys in this order. Readers accept any JSON of the right shape.
export function encodePayload(payload: MessagePayload): Bytes {
  const files = payload.files.map((file) => ({ name: file.name, type: file.type, size: file.size, key: file.key, chunkSize: file.chunkSize }))
  return utf8(JSON.stringify({ text: payload.text, files }))
}

export function decodePayload(bytes: Uint8Array): MessagePayload {
  let parsed: unknown
  try {
    parsed = JSON.parse(fromUtf8(bytes))
  } catch {
    throw new CryptoError('malformed', 'Payload is not JSON')
  }
  if (typeof parsed !== 'object' || parsed === null) throw new CryptoError('malformed', 'Payload has the wrong shape')

  const { text, files } = parsed as { text?: unknown; files?: unknown }
  if (typeof text !== 'string' || !Array.isArray(files)) throw new CryptoError('malformed', 'Payload has the wrong shape')

  return {
    text,
    files: files.map((entry: unknown) => {
      const file = entry as Partial<Record<keyof FileRef, unknown>> | null
      if (
        typeof file !== 'object' || file === null ||
        typeof file.name !== 'string' || typeof file.type !== 'string' || typeof file.key !== 'string' ||
        !Number.isInteger(file.size) || (file.size as number) < 0 ||
        !Number.isInteger(file.chunkSize) || (file.chunkSize as number) < 1
      ) {
        throw new CryptoError('malformed', 'A file entry has the wrong shape')
      }
      if (file.name.length > MAX_FILE_NAME_LENGTH) throw new CryptoError('malformed', 'A file name is too long')
      if ((file.chunkSize as number) > MAX_CHUNK_BYTES) throw new CryptoError('malformed', 'A file chunk size is out of range')
      if (fromBase64Url(file.key).length !== KEY_BYTES) throw new CryptoError('malformed', 'A file key is 32 bytes')
      return { name: file.name, type: file.type, size: file.size as number, key: file.key, chunkSize: file.chunkSize as number }
    }),
  }
}

// Fixed nonce for the published test vectors. Real use leaves it out.
export interface MessageOverrides {
  nonce?: Uint8Array
}

export interface SealMessageInput {
  context: MessageContext
  epoch: number
  epochKey: Uint8Array
  payload: MessagePayload
  signer: KeyPair
}

export async function sealMessage(input: SealMessageInput, overrides: MessageOverrides = {}): Promise<Bytes> {
  const nonce = overrides.nonce ?? randomBytes(NONCE_BYTES)
  const header = concat(Uint8Array.of(VERSION, SUITE_V1), u32(input.epoch), nonce)
  const context = contextBytes(input.context)
  const sealed = await aesGcmEncrypt(input.epochKey, nonce, pad(encodePayload(input.payload)), labelled(LABELS.message, context, header))
  const signature = await ed25519Sign(input.signer.privateKey, messageSignatureInput(context, header, sealed))
  const blob = concat(header, sealed, signature)
  if (blob.length > MAX_MESSAGE_BLOB_BYTES) throw new CryptoError('malformed', 'Message is too large')
  return blob
}

export function messageSignatureInput(context: Uint8Array, header: Uint8Array, sealed: Uint8Array): Bytes {
  return labelled(LABELS.messageSignature, context, header, sealed)
}

function parse(blob: Uint8Array) {
  if (blob.length < MIN_BLOB_BYTES) throw new CryptoError('malformed', 'Message is too short')
  if (blob[0] !== VERSION || blob[1] !== SUITE_V1) throw new CryptoError('unsupported', 'Unknown message version')
  return {
    header: blob.subarray(0, HEADER_BYTES),
    nonce: blob.subarray(6, HEADER_BYTES),
    sealed: blob.subarray(HEADER_BYTES, blob.length - SIGNATURE_BYTES),
    signature: blob.subarray(blob.length - SIGNATURE_BYTES),
  }
}

// The epoch a message was encrypted in, so the right key can be found before opening it.
export function readMessageEpoch(blob: Uint8Array): number {
  parse(blob)
  return readU32(blob, 2)
}

export interface OpenMessageInput {
  context: MessageContext
  epochKey: Uint8Array
  // The identity public key of the sender.
  signerPublic: Uint8Array
  blob: Uint8Array
}

export async function openMessage(input: OpenMessageInput): Promise<MessagePayload> {
  const { header, nonce, sealed, signature } = parse(input.blob)
  const context = contextBytes(input.context)
  if (!(await ed25519Verify(input.signerPublic, messageSignatureInput(context, header, sealed), signature))) {
    throw new CryptoError('authentication', 'Message signature is not valid')
  }
  const padded = await aesGcmDecrypt(input.epochKey, nonce, sealed, labelled(LABELS.message, context, header))
  return decodePayload(unpad(padded))
}
