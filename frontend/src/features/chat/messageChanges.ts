import type { Conversation, LastMessage, Member, Message } from './types'
import { compareTimestamps } from '../../lib/time'

// The rules for messages that change after they were sent, as plain functions so they can be tested without a
// browser. The store holds the messages; these decide which copy of one is the newer, and who may do what.

// How long after sending its sender may edit a message. Matches Messaging:EditWindow on the server, which is the one
// that decides; this only keeps the button from being offered when the answer would be no.
export const EDIT_WINDOW_MS = 15 * 60 * 1000

// The largest a message's text may be, as on the server (SendMessageCommandValidator.MaxContentLength).
export const MAX_MESSAGE_LENGTH = 4000

// How far along a message is. A deleted message is past any edit, so it outranks every count; otherwise the one
// edited more often is the later. Two copies with the same rank are the same version.
function rank(message: Message): number {
  return message.isDeleted ? Number.POSITIVE_INFINITY : message.editCount
}

// True when `candidate` is a later version of the same message than `held`. Pushes and loads race, and a late or
// repeated one must never put back text that was edited away, or text that was deleted.
export function isNewer(candidate: Message, held: Message): boolean {
  return rank(candidate) > rank(held)
}

// Of the copy held and the copy that arrived, the one to keep. A tie goes to the one that arrived: it was read from
// the server later and carries fresh download links.
export function latest(held: Message | undefined, incoming: Message): Message {
  return held && rank(held) > rank(incoming) ? held : incoming
}

// Whether the reader should be offered "Edit": the message is theirs, still has its text, and was sent less than the
// window ago. The window is counted from the sender's own timestamp to the reader's clock, so a wrong clock only
// changes whether the button shows; the server answers for the rest.
export function canEdit(message: Message, selfId: string, now: number): boolean {
  if (message.isDeleted || message.senderId !== selfId) return false

  const age = now - new Date(message.createdAt).getTime()
  return age < EDIT_WINDOW_MS
}

// Whether the reader should be offered "Delete": their own message at any time, or anyone's when they own or administer
// the group. `role` is the reader's role when the member list has been loaded, else undefined.
export function canDelete(message: Message, selfId: string, role: Member['role'] | undefined): boolean {
  if (message.isDeleted) return false

  return message.senderId === selfId || role === 'Owner' || role === 'Admin'
}

// Whether a sidebar preview is the preview of this message: the preview carries no id, so it is recognised by who
// sent it and when.
export function isPreviewOf(preview: LastMessage | null, message: Message): boolean {
  return (
    preview !== null &&
    preview.senderId === message.senderId &&
    compareTimestamps(preview.createdAt, message.createdAt) === 0
  )
}

// The conversation after one of its messages changed: if the sidebar shows that message, it shows the new text (or
// that it was deleted). The same object is returned when the preview is of another message.
export function withChangedPreview(conversation: Conversation, message: Message): Conversation {
  const shown = conversation.lastMessage
  if (!shown || !isPreviewOf(shown, message)) return conversation

  const content = message.isDeleted ? null : message.content
  const attachmentCount = message.isDeleted ? 0 : message.attachments.length
  if (shown.content === content && shown.attachmentCount === attachmentCount) return conversation

  return { ...conversation, lastMessage: { ...shown, content, attachmentCount } }
}

// The conversation after someone else's message was deleted: one fewer unread, if the message was one of them.
//
// Which messages are unread is not stored per message, only as a count, but it is known to be the newest messages
// from others: so a held message is unread when it is among the newest `unreadCount` of them (deleted ones are not
// counted, as the server does not count them). Without the messages, the only one that can be told is the newest
// message of the conversation. `held` is the conversation's messages before the deletion.
export function afterDeletion(
  conversation: Conversation,
  held: Message[] | undefined,
  deleted: Message,
  selfId: string,
): Conversation {
  if (conversation.unreadCount <= 0 || deleted.senderId === selfId) return conversation

  let wasUnread: boolean
  if (held) {
    const others = held.filter((m) => m.senderId !== selfId && !m.isDeleted)
    const index = others.findIndex((m) => m.id === deleted.id)
    wasUnread = index >= 0 && others.length - index <= conversation.unreadCount
  } else {
    wasUnread = isPreviewOf(conversation.lastMessage, deleted)
  }

  return wasUnread ? { ...conversation, unreadCount: conversation.unreadCount - 1 } : conversation
}
