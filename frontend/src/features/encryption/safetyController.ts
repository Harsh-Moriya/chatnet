import { reopenConversation } from '../chat/api/reopen'
import { useChatStore } from '../chat/store/chatStore'
import { epochDeps, readyConversation } from './conversationKeys'
import { useIdentityChanges } from './identityChanges'
import { type SafetyDeps, type SafetyView, acceptChangedIdentity, loadSafetyView, setVerified } from './safetyNumbers'

// Joins the safety numbers (safetyNumbers.ts) to the real server, this browser's stores and the signed-in person. Loaded with the dialog
// that shows them, which carries the cryptography.

function safetyDeps(): SafetyDeps {
  const deps = epochDeps()
  return { api: deps.api, pins: deps.pins, self: { userId: deps.self.userId, identityPublic: deps.self.secrets.identity.publicKey } }
}

export function viewSafetyNumber(contactId: string): Promise<SafetyView> {
  return loadSafetyView(safetyDeps(), contactId)
}

export async function verifyPerson(contactId: string, verified: boolean): Promise<void> {
  await setVerified(safetyDeps(), contactId, verified)
  useIdentityChanges.getState().markedAgain()
}

// The key the person looking was shown becomes the one that is trusted. What was held back by the change is tried again: the
// conversations that could not make their keys for the person, and the messages shown as sent by someone whose key changed.
export async function acceptIdentity(contactId: string, seen: Uint8Array): Promise<void> {
  await acceptChangedIdentity(safetyDeps(), contactId, seen)

  const changes = useIdentityChanges.getState()
  changes.clear(contactId)
  changes.markedAgain()

  for (const conversation of Object.values(useChatStore.getState().conversations)) {
    if (conversation.leftAt !== null) continue

    await readyConversation(conversation.id, { keyEpoch: conversation.keyEpoch, keyRotationRequired: conversation.keyRotationRequired })
    await reopenConversation(conversation.id)
  }
}
