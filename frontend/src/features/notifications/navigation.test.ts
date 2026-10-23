import { describe, expect, it } from 'vitest'
import { conversationPath, focusedMessageId, focusState } from './navigation'

describe('going to the place a notification is about', () => {
  it('opens the conversation', () => {
    expect(conversationPath('c1')).toBe('/chat/c1')
  })

  it('carries the message to show in the navigation state', () => {
    expect(focusedMessageId(focusState('m1'))).toBe('m1')
  })

  it('carries nothing when there is no message', () => {
    expect(focusState(null)).toBeUndefined()
  })

  it('reads nothing from a navigation that did not ask for a message', () => {
    expect(focusedMessageId(undefined)).toBeNull()
    expect(focusedMessageId(null)).toBeNull()
    expect(focusedMessageId({})).toBeNull()
  })

  it('does not trust state that is not the shape it expects', () => {
    expect(focusedMessageId('m1')).toBeNull()
    expect(focusedMessageId({ focusMessageId: 7 })).toBeNull()
    expect(focusedMessageId({ focusMessageId: '' })).toBeNull()
  })
})
