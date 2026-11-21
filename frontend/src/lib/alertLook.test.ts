import { describe, expect, it } from 'vitest'
import { ALERT_BADGE, ALERT_ICON, alertOptions } from './alertLook'

const content = { body: 'Hello', tag: 'conversation-c1', ringing: false, conversationId: 'c1', messageId: 'm1' }

describe('how an alert looks', () => {
  it('carries the picture, the status bar badge, the words and where a click goes', () => {
    expect(alertOptions(content, 1000)).toMatchObject({
      body: 'Hello',
      icon: ALERT_ICON,
      badge: ALERT_BADGE,
      data: { conversationId: 'c1', messageId: 'm1' },
      timestamp: 1000,
    })
  })

  it('alerts again for a new message under the tag already on the screen', () => {
    expect(alertOptions(content, 0)).toMatchObject({ tag: 'conversation-c1', renotify: true })
  })

  it('has no tag and no renotify when there is no tag, which a browser refuses to show otherwise', () => {
    const options = alertOptions({ ...content, tag: '' }, 0)

    expect(options).not.toHaveProperty('tag')
    expect(options).not.toHaveProperty('renotify')
  })

  it('vibrates for longer for a ringing call than for a message', () => {
    const total = (pattern: number[]) => pattern.reduce((sum, ms) => sum + ms, 0)

    expect(total(alertOptions({ ...content, ringing: true }, 0).vibrate)).toBeGreaterThan(total(alertOptions(content, 0).vibrate))
  })

  it('keeps a ringing call on the screen until it is dealt with, and no other alert', () => {
    expect(alertOptions({ ...content, ringing: true }, 0).requireInteraction).toBe(true)
    expect(alertOptions(content, 0)).not.toHaveProperty('requireInteraction')
  })

  it('has no ids when the alert is not about a conversation', () => {
    expect(alertOptions({ ...content, conversationId: null, messageId: null }, 0).data).toEqual({ conversationId: null, messageId: null })
  })
})
