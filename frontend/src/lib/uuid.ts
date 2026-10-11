// An id for a message the browser is about to send, so the server's answer can be matched to the
// unconfirmed copy on screen. It does not need to be secret, only unique.
//
// crypto.randomUUID is only available in secure contexts (https, or localhost). Opening the app over
// plain http from another device on the local network is not one, so there is a fallback built from
// crypto.getRandomValues, which works everywhere.
export function newClientMessageId(): string {
  if (typeof crypto.randomUUID === 'function') return crypto.randomUUID()

  const bytes = crypto.getRandomValues(new Uint8Array(16))
  bytes[6] = (bytes[6] & 0x0f) | 0x40 // version 4
  bytes[8] = (bytes[8] & 0x3f) | 0x80 // variant 10xx

  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}
