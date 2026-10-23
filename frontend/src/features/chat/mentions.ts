import type { Member } from './types'

// Mentioning someone is writing "@userName" in a message. The server reads the same text with the same rule
// (MentionParser) to decide who is notified, so this file and that one must agree: a name is 3 to 50 letters,
// digits or underscores, and the "@" must not be glued to a word before it (so an email address is not a mention).
const NAME_CHARS = '[A-Za-z0-9_]'
const MENTION = new RegExp(`(?<!${NAME_CHARS})@(${NAME_CHARS}{3,50})(?!${NAME_CHARS})`, 'g')

// The most suggestions the list under the box shows.
export const MAX_SUGGESTIONS = 6

// ##### Showing mentions #####

export interface TextPart {
  text: string
  // Set when this part is a mention of a member of the conversation.
  mention?: { userName: string; isSelf: boolean }
}

// The message text cut into plain parts and mentions. Only a name that belongs to a member of the conversation
// counts: the server notifies members only, so "@nobody" is plain text.
export function splitMentions(content: string, members: readonly Member[] | undefined, selfUserName: string | undefined): TextPart[] {
  if (!members || members.length === 0) return [{ text: content }]

  const byName = new Map(members.map((m) => [m.userName.toLowerCase(), m.userName]))
  const parts: TextPart[] = []
  let from = 0

  for (const match of content.matchAll(MENTION)) {
    const userName = byName.get(match[1].toLowerCase())
    if (!userName) continue

    if (match.index > from) parts.push({ text: content.slice(from, match.index) })
    parts.push({
      text: match[0],
      mention: { userName, isSelf: userName.toLowerCase() === selfUserName?.toLowerCase() },
    })
    from = match.index + match[0].length
  }

  if (from < content.length) parts.push({ text: content.slice(from) })
  return parts.length > 0 ? parts : [{ text: content }]
}

// ##### Writing mentions #####

// The "@name" being typed: where its "@" is, and the letters after it so far. Null when the cursor is not at the
// end of one.
export interface ActiveMention {
  start: number
  query: string
}

export function activeMention(text: string, caret: number): ActiveMention | null {
  const before = text.slice(0, caret)
  const match = new RegExp(`(?<!${NAME_CHARS})@(${NAME_CHARS}{0,50})$`).exec(before)

  return match ? { start: match.index, query: match[1] } : null
}

// The members whose name fits what has been typed, best first: names that start with it, then display names
// that contain it. The signed-in user is not offered, since a message to oneself notifies nobody.
export function suggestMembers(
  members: readonly Member[] | undefined,
  query: string,
  selfUserId: string | undefined,
  limit = MAX_SUGGESTIONS,
): Member[] {
  const wanted = query.toLowerCase()
  const candidates = (members ?? []).filter((m) => m.userId !== selfUserId)

  const rank = (m: Member): number => {
    if (wanted === '') return 2
    if (m.userName.toLowerCase().startsWith(wanted)) return 0
    if (m.displayName.toLowerCase().startsWith(wanted)) return 1
    if (m.userName.toLowerCase().includes(wanted) || m.displayName.toLowerCase().includes(wanted)) return 2
    return 3
  }

  return candidates
    .filter((m) => rank(m) < 3)
    .sort((a, b) => rank(a) - rank(b) || a.userName.localeCompare(b.userName))
    .slice(0, limit)
}

// The text after choosing a suggestion: the typed "@query" becomes "@userName" followed by a space, so typing goes
// on, and the caret goes after the space. A space that is already there is used instead of adding a second one.
export function applyMention(
  text: string,
  mention: ActiveMention,
  caret: number,
  userName: string,
): { text: string; caret: number } {
  const after = text.slice(caret)
  const spaceFollows = /^\s/.test(after)
  const inserted = `@${userName}${spaceFollows ? '' : ' '}`

  return {
    text: text.slice(0, mention.start) + inserted + after,
    caret: mention.start + `@${userName} `.length,
  }
}
