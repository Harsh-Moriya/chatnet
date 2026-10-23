import type { Conversation, Message } from '../chat/types'
import type { NotificationItem } from './types'

// When the browser shows an alert (an operating system notification) and what it says. Pure functions, so the
// rules can be tested without a browser; browserAlert.ts is the part that talks to the browser.

// What the user is looking at right now. `looking` is true while the page is in front of them and the window has
// the focus: a visible tab in a window they have moved away from is not being looked at.
export interface Viewing {
  looking: boolean
  openConversationId: string | null
}

// A conversation whose messages are in front of the reader needs no alert: the message is already on screen.
function onScreen(conversationId: string | null, view: Viewing): boolean {
  return view.looking && conversationId !== null && view.openConversationId === conversationId
}

// A new message from someone else, unless the conversation is muted or is the one being read.
export function shouldAlertForMessage(
  message: Message,
  selfId: string | null,
  conversation: Conversation | undefined,
  view: Viewing,
): boolean {
  if (message.senderId === selfId) return false
  if (conversation?.isMuted) return false

  return !onScreen(message.conversationId, view)
}

// A mention alerts even in a muted conversation: someone addressed the user by name. Being added to a group is
// only worth an alert when the user is not looking; the bell and the sidebar already show it to someone who is.
export function shouldAlertForNotification(notification: NotificationItem, view: Viewing): boolean {
  if (notification.type === 'Mention') return !onScreen(notification.conversationId, view)

  return !view.looking
}

export interface AlertText {
  title: string
  body: string
  // Alerts with the same tag replace each other, so a conversation shows one alert at a time and a mention
  // that follows the message it is in does not stack up.
  tag: string
}

const PREVIEW_LENGTH = 100

function preview(text: string): string {
  const trimmed = text.trim()
  return trimmed.length > PREVIEW_LENGTH ? `${trimmed.slice(0, PREVIEW_LENGTH).trimEnd()}…` : trimmed
}

export function conversationTag(conversationId: string): string {
  return `conversation-${conversationId}`
}

export function messageAlertText(message: Message, conversation: Conversation | undefined): AlertText {
  const inGroup = conversation?.type === 'Group'
  const files = message.attachments.length

  return {
    title: inGroup ? `${message.senderDisplayName} in ${conversation.name}` : message.senderDisplayName,
    body: message.content ? preview(message.content) : files > 0 ? (files === 1 ? 'Sent a file' : `Sent ${files} files`) : '',
    tag: conversationTag(message.conversationId),
  }
}

// `conversationName` is what the sidebar calls the conversation; undefined when it is not known there.
export function notificationAlertText(notification: NotificationItem, conversationName: string | undefined): AlertText {
  const tag = notification.conversationId ? conversationTag(notification.conversationId) : `notification-${notification.id}`
  const actor = notification.actorDisplayName

  if (notification.type === 'Mention') {
    return {
      title: `${actor} mentioned you in ${conversationName ?? 'a conversation'}`,
      body: notification.messagePreview ? preview(notification.messagePreview) : '',
      tag,
    }
  }

  return { title: `${actor} added you to ${conversationName ?? 'a group'}`, body: '', tag }
}
