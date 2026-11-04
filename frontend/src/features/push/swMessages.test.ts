import { describe, expect, it } from 'vitest'
import { parseOpenConversation } from './swMessages'

const CONVERSATION = '7d3c1f0e-1b2a-4c3d-8e4f-5a6b7c8d9e0f'
const MESSAGE = '0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d'

describe('a request from the service worker to open a conversation', () => {
  it('is read with the conversation and the message', () => {
    expect(parseOpenConversation({ type: 'open-conversation', conversationId: CONVERSATION, messageId: MESSAGE })).toEqual({
      conversationId: CONVERSATION,
      messageId: MESSAGE,
    })
  })

  it('is read without a message when the alert had none', () => {
    expect(parseOpenConversation({ type: 'open-conversation', conversationId: CONVERSATION, messageId: null })).toEqual({
      conversationId: CONVERSATION,
      messageId: null,
    })
    expect(parseOpenConversation({ type: 'open-conversation', conversationId: CONVERSATION })).toEqual({
      conversationId: CONVERSATION,
      messageId: null,
    })
  })

  it('is read without a message that is not an id', () => {
    expect(parseOpenConversation({ type: 'open-conversation', conversationId: CONVERSATION, messageId: '../x' })).toEqual({
      conversationId: CONVERSATION,
      messageId: null,
    })
  })

  it.each([
    ['nothing', undefined],
    ['null', null],
    ['text', 'open-conversation'],
    ['a message of another kind', { type: 'something-else', conversationId: CONVERSATION }],
    ['no type', { conversationId: CONVERSATION }],
    ['no conversation', { type: 'open-conversation' }],
    ['a conversation that is not an id', { type: 'open-conversation', conversationId: '../../admin' }],
    ['a conversation that is a number', { type: 'open-conversation', conversationId: 7 }],
  ])('is nothing for %s', (_, data) => {
    expect(parseOpenConversation(data)).toBeNull()
  })
})
