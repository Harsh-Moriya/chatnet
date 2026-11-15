// The codes the server refuses a call method with (CallErrors on the server): the message of the HubException the client library throws. The
// library puts its own words around the message, so the code is found inside it.

export const CALL_ERROR_CODES = [
  'not-a-member',
  'not-a-direct-conversation',
  'peer-unavailable',
  'already-in-a-call',
  'callee-busy',
  'rate-limited',
  'calls-unavailable',
  'call-not-found',
  'call-not-ringing',
  'not-the-callee',
  'not-in-the-call',
  'call-ended',
  'bad-token',
  'signal-too-large',
  'signal-limit',
] as const

export type CallErrorCode = (typeof CALL_ERROR_CODES)[number]

export function callErrorCode(error: unknown): CallErrorCode | null {
  const message = error instanceof Error ? error.message : typeof error === 'string' ? error : ''

  // The longest match first: no code is a prefix of another today, but a later one may be.
  return [...CALL_ERROR_CODES].sort((a, b) => b.length - a.length).find((code) => message.includes(code)) ?? null
}
