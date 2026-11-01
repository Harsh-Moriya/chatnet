// These mirror the JSON the API sends (see section 6.3 of the architecture overview). Enums are
// strings, timestamps are ISO 8601 UTC strings, and IDs are opaque UUID strings.

export type ConversationType = 'Direct' | 'Group'

// A conversation as the signed-in user sees it: for a direct chat, `name` is the other person.
export interface Conversation {
  id: string
  type: ConversationType
  name: string
  avatarUrl: string | null
  updatedAt: string
  lastMessage: LastMessage | null
  // Messages from other people since you last read it. Counted by the server up to a cap
  // (UNREAD_CAP), then increased here by each message that arrives.
  unreadCount: number
  // The other person of a direct conversation, whose presence is shown for it; null for a group.
  otherUserId: string | null
  // The signed-in user's own choice to have this conversation quiet: no alert for its new messages, and its
  // unread count stays out of the page title. A mention still alerts.
  isMuted: boolean
  // When the signed-in user left the group, or was removed from it; null while they are in it. A conversation they
  // left stays in the list, read-only, showing what was said up to this moment.
  leftAt: string | null
}

export interface LastMessage {
  senderId: string
  // Null when the last message was deleted. Empty when it was only files.
  content: string | null
  createdAt: string
  // How many files the last message carries, so a message of only files can still be previewed.
  attachmentCount: number
}

export type AttachmentType = 'Image' | 'Video' | 'File'

// A file attached to a confirmed message. `url` is a link signed by the server that stops working after a
// while (see refreshAttachmentUrl), so it is never kept beyond the page that shows it.
export interface Attachment {
  id: string
  type: AttachmentType
  fileName: string
  contentType: string
  sizeBytes: number
  url: string
}

export interface Message {
  id: string
  conversationId: string
  senderId: string
  senderDisplayName: string
  // Null for a deleted message: it keeps its place in the timeline but has no text. A message of only files
  // has an empty string.
  content: string | null
  createdAt: string
  attachments: Attachment[]
  // The id the sender's tab invented for its optimistic copy: on the response to a send, on its push, and
  // in history. Null for a message sent without one.
  clientMessageId: string | null
  // When the sender last edited it, or null if they never did. `editCount` is how many times, and orders two copies of
  // the same message: the higher is the newer. A deleted message is newer than any other copy of itself.
  editedAt: string | null
  editCount: number
  isDeleted: boolean
}

// A file that finished uploading and is attached to a message that has not been confirmed yet. `uploadKey` is
// what the server gave for it; the message names it so the server can find the uploaded file. The file itself
// is kept to draw a preview of an image while the message is unconfirmed.
export interface PendingAttachment {
  uploadKey: string
  fileName: string
  contentType: string
  sizeBytes: number
  type: AttachmentType
  file: File
}

// A message this tab has sent that the server has not confirmed yet. It lives only in the
// browser; once the server's copy arrives it is replaced by it.
export interface PendingMessage {
  clientMessageId: string
  conversationId: string
  content: string
  attachments: PendingAttachment[]
  // The browser's clock at the moment of sending. Used for display only.
  createdAt: string
  status: 'sending' | 'failed'
  error: string | null
}

// A person in a conversation and how far they have read: they have seen every message sent at or
// before `lastReadAt`. Null until they have read or sent something.
export interface Member {
  userId: string
  // The name a message addresses the person by ("@userName").
  userName: string
  displayName: string
  avatarUrl: string | null
  role: 'Owner' | 'Admin' | 'Member'
  lastReadAt: string | null
}

// What changed about the people of a group, as the server pushes it: the members whose entry is new or changed (as it is
// now), and the people who are no longer in the group. Applying it twice, or after a later change, gives the same list.
export interface MembersChange {
  conversationId: string
  members: Member[]
  removedUserIds: string[]
}

// The parts of a group that its members share and any of them may change: what it is called, and its picture.
export interface ConversationUpdate {
  id: string
  name: string
  avatarUrl: string | null
}

export interface UserSummary {
  id: string
  userName: string
  displayName: string
  avatarUrl: string | null
}

export interface TypingUser {
  userId: string
  displayName: string
}

export type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting' | 'disconnected'
