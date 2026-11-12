import { describe, expect, it } from 'vitest'
import { PREVIEW_LENGTH, previewOf } from './preview'
import type { Unreadable } from './types'
import { unreadableText } from './unreadable'

describe('what is said where a message cannot be shown', () => {
  const reasons: Unreadable[] = ['pending', 'noKey', 'invalid', 'senderUnknown', 'senderChanged']

  it('has a line for every reason, and each is different', () => {
    const lines = reasons.map(unreadableText)

    expect(new Set(lines).size).toBe(reasons.length)
    expect(lines.every((line) => line.length > 0)).toBe(true)
  })

  it('says that a message still being opened is encrypted', () => {
    expect(unreadableText('pending')).toBe('Encrypted message')
  })

  it('says that a message whose key is not here cannot be decrypted on this device', () => {
    expect(unreadableText('noKey')).toContain("can't be decrypted on this device")
  })

  it('says that a message that failed its checks cannot be verified, and who is to blame when that is known', () => {
    expect(unreadableText('invalid')).toContain("can't be verified")
    expect(unreadableText('senderUnknown')).toContain('no keys')
    expect(unreadableText('senderChanged')).toContain('changed')
  })

  it('does not use a dash, as no text of the app does', () => {
    const text = reasons.map(unreadableText).join('')

    expect(text).not.toContain(String.fromCharCode(0x2013))
    expect(text).not.toContain(String.fromCharCode(0x2014))
  })
})

describe('the start of a text, for a line of it', () => {
  it('is the text itself when it is short', () => {
    expect(previewOf('hello')).toBe('hello')
    expect(previewOf('x'.repeat(PREVIEW_LENGTH))).toBe('x'.repeat(PREVIEW_LENGTH))
  })

  it('is cut at the length of a preview when it is longer', () => {
    expect(previewOf('x'.repeat(PREVIEW_LENGTH + 1))).toBe('x'.repeat(PREVIEW_LENGTH))
  })

  it('stays empty when there is nothing, and null when there is no text', () => {
    expect(previewOf('')).toBe('')
    expect(previewOf(null)).toBeNull()
  })
})
