// These mirror the JSON the API sends (see section 6.3 of the architecture overview).

export type NotificationType = 'NewMessage' | 'Mention' | 'MemberAdded'

// Something the bell lists: another person mentioned the signed-in user, or added them to a group.
export interface NotificationItem {
  id: string
  type: NotificationType
  // Null once the conversation it was about is gone.
  conversationId: string | null
  actorId: string
  actorDisplayName: string
  // The message that mentions the user. Null for an invitation, and once the message is gone.
  messageId: string | null
  // The start of that message's text. Null for an invitation, and for a message that was deleted.
  messagePreview: string | null
  isRead: boolean
  createdAt: string
}

// A notification as the server sends it: the message a mention is about is sealed, and the browser opens it to show the start of what
// was said. Null for an invitation, and once the message was deleted. The person who sealed it is the actor.
export type NotificationWire = Omit<NotificationItem, 'messagePreview'> & {
  message: { ciphertext: string; keyEpoch: number; clientMessageId: string; editCount: number } | null
}

// One page of the list, newest first, with how many are unread in all (the server stops counting at its
// cap, so a large number means "at least this many").
export interface NotificationList {
  items: NotificationItem[]
  unreadCount: number
}

export type NotificationListWire = Omit<NotificationList, 'items'> & { items: NotificationWire[] }
