import { compareTimestamps } from '../../lib/time'
import { previewOf } from './preview'
import type { Attachment, Conversation, LastMessage, Member, Message, SealedPreview } from './types'

// The rules for messages that change after they were sent, as plain functions so they can be tested without a
// browser. The store holds the messages; these decide which copy of one is the newer, and who may do what.

// How long after sending its sender may edit a message. Matches Messaging:EditWindow on the server, which is the one
// that decides; this only keeps the button from being offered when the answer would be no.
export const EDIT_WINDOW_MS = 15 * 60 * 1000

// The largest a message's text may be. The server cannot read the text, so it is this side that keeps to it (the server only keeps a
// sealed message within the largest size the formats allow, which 4000 characters of any language fit in).
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
// the server later and carries fresh download links. Its text is the held one's when the held copy could be opened and the one that
// arrived could not (the same version of the message, which the keys of this browser opened a moment ago).
export function latest(held: Message | undefined, incoming: Message): Message {
  if (held && rank(held) > rank(incoming)) return held

  const lostItsText = held && rank(held) === rank(incoming) && incoming.content === null && !incoming.isDeleted && held.content !== null
  return lostItsText ? { ...incoming, content: held.content, unreadable: null, attachments: withEntriesOf(held.attachments, incoming.attachments) } : incoming
}

// The attachments of the copy that arrived (their links are the fresher), with the entries the held copy was opened with, by attachment.
function withEntriesOf(held: Attachment[], incoming: Attachment[]): Attachment[] {
  return incoming.map((attachment) => ({ ...attachment, file: attachment.file ?? held.find((h) => h.id === attachment.id)?.file ?? null }))
}

// What a message was sealed as, to open it again; null for a message that was deleted, or made here and not confirmed.
export function sealedPreviewOf(message: Message): SealedPreview | null {
  if (message.isDeleted || message.ciphertext == null || message.keyEpoch === undefined || message.clientMessageId === null) return null

  return {
    ciphertext: message.ciphertext,
    keyEpoch: message.keyEpoch,
    clientMessageId: message.clientMessageId,
    editCount: message.editCount,
  }
}

// The preview of a conversation that shows this message.
export function previewFrom(message: Message): LastMessage {
  return {
    senderId: message.senderId,
    content: message.isDeleted ? null : previewOf(message.content),
    unreadable: message.isDeleted ? null : (message.unreadable ?? null),
    createdAt: message.createdAt,
    attachmentCount: message.isDeleted ? 0 : message.attachments.length,
    sealed: sealedPreviewOf(message),
  }
}

// Whether the reader should be offered "Edit": the message is theirs, still has its text, and was sent less than the
// window ago. The window is counted from the sender's own timestamp to the reader's clock, so a wrong clock only
// changes whether the button shows; the server answers for the rest.
export function canEdit(message: Message, selfId: string, now: number): boolean {
  if (message.isDeleted || message.senderId !== selfId) return false

  // An edit seals the message again with its text and the entries of its files. A message that could not be opened has neither, and one
  // with a file the sealed message does not vouch for has an entry missing, which would put the others out of place: it is not offered.
  if (message.unreadable || message.attachments.some((attachment) => attachment.file === null)) return false

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

  const next = previewFrom(message)
  const same =
    shown.content === next.content &&
    shown.attachmentCount === next.attachmentCount &&
    (shown.unreadable ?? null) === next.unreadable &&
    shown.sealed?.editCount === next.sealed?.editCount
  if (same) return conversation

  return { ...conversation, lastMessage: next }
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
