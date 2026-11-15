import { describe, expect, it } from 'vitest'
import type { FileRef } from '../../../lib/crypto/message'
import type { Attachment, Message } from '../types'
import { deletedIdsOf, galleryItemsOf } from './galleryItems'

const entry = (name: string, type = 'image/png'): FileRef => ({ name, type, size: 100, key: 'k'.repeat(43), chunkSize: 65536 })

const attachment = (id: string, file: FileRef | null): Attachment => ({ id, sizeBytes: 129, url: `https://files.test/${id}`, file })

const message = (id: string, attachments: Attachment[], overrides: Partial<Message> = {}): Message => ({
  id,
  conversationId: 'c1',
  senderId: 'u1',
  senderDisplayName: 'Ana',
  content: '',
  createdAt: '2026-11-14T10:00:00Z',
  attachments,
  clientMessageId: null,
  editedAt: null,
  editCount: 0,
  isDeleted: false,
  ...overrides,
})

describe('the files of the gallery', () => {
  it('lists the files of every page loaded, in the order the pages and the messages came: newest first', () => {
    const items = galleryItemsOf(
      [
        [message('m9', [attachment('a9', entry('nine.png'))]), message('m8', [attachment('a8', entry('eight.pdf', 'application/pdf'))])],
        [message('m2', [attachment('a2', entry('two.mp4', 'video/mp4'))])],
      ],
      new Set(),
    )

    expect(items.map((item) => [item.source.id, item.kind])).toEqual([
      ['a9', 'Image'],
      ['a8', 'Pdf'],
      ['a2', 'Video'],
    ])
  })

  it('leaves out a message the chat knows is deleted, and keeps the others of its page', () => {
    const items = galleryItemsOf(
      [[message('m9', [attachment('a9', entry('nine.png'))]), message('m8', [attachment('a8', entry('eight.png'))])]],
      new Set(['m9']),
    )

    expect(items.map((item) => item.source.id)).toEqual(['a8'])
  })

  it('leaves out files that cannot be shown: other types, and files the message does not vouch for', () => {
    const items = galleryItemsOf(
      [[message('m1', [attachment('a1', entry('notes.docx', 'application/zip')), attachment('a2', null), attachment('a3', entry('logo.svg', 'image/svg+xml'))])]],
      new Set(),
    )

    expect(items).toEqual([])
  })

  it('has nothing for a gallery that has no pages yet', () => {
    expect(galleryItemsOf([], new Set())).toEqual([])
  })
})

describe('the messages the chat holds as deleted', () => {
  it('collects the ids of the deleted ones only', () => {
    const held = [message('m1', []), message('m2', [], { isDeleted: true }), message('m3', [], { isDeleted: true })]

    expect(deletedIdsOf(held)).toEqual(new Set(['m2', 'm3']))
  })

  it('has none for a conversation the chat holds nothing of', () => {
    expect(deletedIdsOf(undefined)).toEqual(new Set())
  })
})
