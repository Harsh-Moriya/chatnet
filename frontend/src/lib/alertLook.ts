// How an alert looks and behaves on the screen, shared by the two places that raise one: the service worker, for a push that arrives while
// the app is closed, and the page, for what arrives over its connection while it is open but not in front. They must not drift apart, so the
// rules are here and both import them. No browser objects: this file is compiled into the worker as well.

export const ALERT_ICON = '/icons/icon-192.png'
// The small single-colour picture an Android phone shows in its status bar (white on transparent).
export const ALERT_BADGE = '/icons/badge-96.png'

// Milliseconds on and off, as a phone's vibration motor takes them: a short double tap for a message, a long ring for a call.
const MESSAGE_VIBRATION = [120, 60, 120]
const CALL_VIBRATION = [500, 250, 500, 250, 500]

export interface AlertContent {
  body: string
  // Alerts with the same tag replace each other, so a conversation shows one at a time. Empty for none.
  tag: string
  // A call that is ringing: it stays on the screen until it is dealt with, and rings the phone longer.
  ringing: boolean
  conversationId: string | null
  messageId: string | null
}

export interface AlertOptions {
  body: string
  tag?: string
  data: { conversationId: string | null; messageId: string | null }
  icon: string
  badge: string
  vibrate: number[]
  // A new alert under a tag that is already on the screen replaces it and, with this, alerts again (sound and vibration); without it the
  // replacement is silent. Only allowed with a tag.
  renotify?: boolean
  timestamp: number
  requireInteraction?: boolean
}

export function alertOptions(content: AlertContent, now: number): AlertOptions {
  const tag = content.tag === '' ? undefined : content.tag

  return {
    body: content.body,
    ...(tag === undefined ? {} : { tag, renotify: true }),
    data: { conversationId: content.conversationId, messageId: content.messageId },
    icon: ALERT_ICON,
    badge: ALERT_BADGE,
    vibrate: content.ringing ? CALL_VIBRATION : MESSAGE_VIBRATION,
    timestamp: now,
    ...(content.ringing ? { requireInteraction: true } : {}),
  }
}
