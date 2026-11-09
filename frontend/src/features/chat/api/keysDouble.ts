import { vi } from 'vitest'

// For the tests of the chat API: the parts of the conversation keys that need a signed-in person, a server and this browser's stores,
// replaced by stand-ins that record how they were called. The rest of the module (the retries, the mapping of a refused epoch) is the
// real one. The keys themselves are tested against the real cryptography in features/encryption.

export const deps = { name: 'deps' } as never

export const prepared = {
  conversationId: 'made-id',
  body: { epoch: 1, commitment: 'commitment', signature: 'signature', envelopes: [{ userId: 'u1', generation: 1, envelope: 'envelope' }] },
  key: new Uint8Array(32).fill(7),
}

export const addition = {
  body: { epoch: 2, commitment: 'commitment-2', signature: 'signature-2', envelopes: [{ userId: 'u1', generation: 1, envelope: 'envelope-2' }] },
  key: new Uint8Array(32).fill(9),
}

export const keysDouble = {
  epochDeps: vi.fn(() => deps),
  prepareNewConversation: vi.fn(async () => prepared),
  keepNewConversationKey: vi.fn(async () => undefined),
  prepareAddingMembers: vi.fn(async () => addition),
  saveKey: vi.fn(async () => undefined),
}

export function resetKeysDouble(): void {
  for (const double of Object.values(keysDouble)) double.mockClear()
}
