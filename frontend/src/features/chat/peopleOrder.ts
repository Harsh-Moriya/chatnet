import type { Conversation, UserSummary } from './types'

// The people the signed-in user already has a direct conversation with.
export function contactIdsOf(conversations: Record<string, Conversation>): ReadonlySet<string> {
  const ids = new Set<string>()
  for (const conversation of Object.values(conversations)) {
    if (conversation.type === 'Direct' && conversation.otherUserId !== null) ids.add(conversation.otherUserId)
  }
  return ids
}

// People to start something with first, then the ones already chatted with, each group in the order the server gave (by name).
export function newPeopleFirst(people: readonly UserSummary[], contactIds: ReadonlySet<string>): UserSummary[] {
  return [...people.filter((person) => !contactIds.has(person.id)), ...people.filter((person) => contactIds.has(person.id))]
}
