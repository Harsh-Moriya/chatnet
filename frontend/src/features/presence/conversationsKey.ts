// The conversations a user is in, as one string that changes when one is added or removed and not when
// anything inside one changes (a new message, a read position). The people whose presence matters are
// the members of these conversations, so a load of presence keyed by this string runs again exactly when
// the set of people may have changed. The order the conversations were added in does not matter.
export function conversationsKey(conversationIds: string[]): string {
  return [...conversationIds].sort().join(',')
}
