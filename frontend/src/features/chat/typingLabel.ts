import type { TypingUser } from './types'

// The sentence shown under a conversation's name and above the composer while others type.
export function typingLabel(users: TypingUser[] | undefined): string | null {
  if (!users || users.length === 0) return null
  if (users.length === 1) return `${users[0].displayName} is typing`
  if (users.length === 2) return `${users[0].displayName} and ${users[1].displayName} are typing`
  return 'Several people are typing'
}
