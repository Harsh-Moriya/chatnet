import type { Unreadable } from './types'

// What is shown where the text of a message should be, when it cannot be: in the conversation, and as the preview in the list. A
// message that is not shown is no reason to stop reading the rest, so each says in a line what is wrong with this one.
export function unreadableText(reason: Unreadable): string {
  switch (reason) {
    case 'pending':
      return 'Encrypted message'
    case 'noKey':
      return "This message can't be decrypted on this device"
    case 'invalid':
      return "This message can't be verified"
    case 'senderUnknown':
      return "This message can't be verified: its sender has no keys"
    case 'senderChanged':
      return "This message can't be verified: its sender's security key changed"
  }
}
