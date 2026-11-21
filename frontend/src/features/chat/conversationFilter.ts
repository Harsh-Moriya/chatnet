// Letters without their accents and without case, so "jose" finds "José" and "ben" finds "BEN". Spaces at the ends are not part of what is wanted.
function plain(text: string): string {
  return text.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase().trim()
}

// The conversations whose name has what was typed in it: the other person's display name for a direct conversation, the group's name for a group.
// Nothing typed is every conversation, as the same list (so a screen showing it does not draw again).
export function filterConversations<T extends { name: string }>(conversations: readonly T[], typed: string): readonly T[] {
  const wanted = plain(typed)
  if (wanted === '') return conversations

  return conversations.filter((conversation) => plain(conversation.name).includes(wanted))
}
