// How a notification (or its alert) takes the user to the place it is about: the conversation, and for a mention
// the message, which the list scrolls to once it is loaded. The message travels in the navigation state, not the
// URL, so a reload or a shared link opens the conversation plainly.

export interface FocusState {
  focusMessageId: string
}

export function conversationPath(conversationId: string): string {
  return `/chat/${conversationId}`
}

// The state to navigate with; none when there is no message to point at.
export function focusState(messageId: string | null): FocusState | undefined {
  return messageId ? { focusMessageId: messageId } : undefined
}

// The message a navigation asked to be shown, or null. The state is whatever the previous page put there, so it
// is checked rather than trusted.
export function focusedMessageId(state: unknown): string | null {
  if (typeof state !== 'object' || state === null) return null

  const { focusMessageId } = state as Partial<FocusState>
  return typeof focusMessageId === 'string' && focusMessageId !== '' ? focusMessageId : null
}
