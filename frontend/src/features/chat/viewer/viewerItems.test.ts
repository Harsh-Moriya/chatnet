import { describe, expect, it } from 'vitest'
import type { FileRef } from '../../../lib/crypto/message'
import type { Attachment, Message } from '../types'
import { indexOfItem, neighbour, viewerItemsOf } from './viewerItems'

const entry = (name: string, type = 'image/png', size = 100): FileRef => ({ name, type, size, key: 'k'.repeat(43), chunkSize: 65536 })

const attachment = (id: string, file: FileRef | null): Attachment => ({ id, sizeBytes: 129, url: `https://files.test/${id}`, file })

function message(id: string, attachments: Attachment[], overrides: Partial<Message> = {}): Message {
  return {
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
  }
}

describe('the pictures, the PDFs and the videos of a conversation', () => {
  it('lists them in the order of the messages, and in the order of the files of a message', () => {
    const items = viewerItemsOf([
      message('m1', [attachment('a1', entry('one.png')), attachment('a2', entry('two.jpg', 'image/jpeg'))]),
      message('m2', [attachment('a3', entry('three.webp', 'image/webp'))]),
    ])

    expect(items.map((item) => item.source.id)).toEqual(['a1', 'a2', 'a3'])
  })

  it('lists a PDF with the pictures, with its kind, whatever case or parameters the sender wrote', () => {
    const items = viewerItemsOf([
      message('m1', [attachment('a1', entry('one.png')), attachment('a2', entry('plan.pdf', 'Application/PDF; version=1.7'))]),
      message('m2', [attachment('a3', entry('two.jpg', 'image/jpeg'))]),
    ])

    expect(items.map((item) => [item.source.id, item.kind])).toEqual([
      ['a1', 'Image'],
      ['a2', 'Pdf'],
      ['a3', 'Image'],
    ])
  })

  it('lists a video with the others, with its kind, whatever case or parameters the sender wrote', () => {
    const items = viewerItemsOf([
      message('m1', [attachment('a1', entry('one.png')), attachment('a2', entry('clip.mp4', 'Video/MP4; codecs=avc1')), attachment('a3', entry('talk.webm', 'video/webm'))]),
    ])

    expect(items.map((item) => [item.source.id, item.kind])).toEqual([
      ['a1', 'Image'],
      ['a2', 'Video'],
      ['a3', 'Video'],
    ])
  })

  it('carries what is needed to open each one, who sent it and when', () => {
    const [item] = viewerItemsOf([message('m1', [attachment('a1', entry('map.png', 'image/png', 4321))], { senderDisplayName: 'Ben', createdAt: '2026-11-14T11:30:00Z' })])

    expect(item).toEqual({
      kind: 'Image',
      source: { id: 'a1', conversationId: 'c1', messageId: 'm1', url: 'https://files.test/a1', entry: entry('map.png', 'image/png', 4321) },
      name: 'map.png',
      size: 4321,
      senderName: 'Ben',
      sentAt: '2026-11-14T11:30:00Z',
    })
  })

  it('makes the name harmless, as the message does', () => {
    const [item] = viewerItemsOf([message('m1', [attachment('a1', entry('C:\\Users\\me\\evil\u202Egnp.exe'))])])

    expect(item.name).toBe('evilgnp.exe')
  })

  it.each(['video/x-msvideo', 'video/quicktime', 'application/x-pdf', 'text/plain', 'application/zip', 'image/svg+xml', 'image/x-icon', ''])('does not list a file that says it is %s', (type) => {
    expect(viewerItemsOf([message('m1', [attachment('a1', entry('f', type))])])).toEqual([])
  })

  it('does not list a file the sealed message does not vouch for', () => {
    expect(viewerItemsOf([message('m1', [attachment('a1', null), attachment('a2', entry('ok.png'))])]).map((item) => item.source.id)).toEqual(['a2'])
  })

  it('lists nothing of a deleted message', () => {
    expect(viewerItemsOf([message('m1', [attachment('a1', entry('gone.png'))], { isDeleted: true, content: null })])).toEqual([])
  })

  it('lists nothing for no messages or messages without files', () => {
    expect(viewerItemsOf([])).toEqual([])
    expect(viewerItemsOf([message('m1', [])])).toEqual([])
  })
})

describe('moving from one picture to the next', () => {
  const items = viewerItemsOf([message('m1', [attachment('a1', entry('1.png')), attachment('a2', entry('2.png')), attachment('a3', entry('3.png'))])])

  it('finds the one after and the one before', () => {
    expect(neighbour(items, 'a2', 1)?.source.id).toBe('a3')
    expect(neighbour(items, 'a2', -1)?.source.id).toBe('a1')
  })

  it('goes from a picture to a video to a PDF, as they are in one list', () => {
    const mixed = viewerItemsOf([message('m1', [attachment('a1', entry('1.png')), attachment('a2', entry('2.mp4', 'video/mp4')), attachment('a3', entry('3.pdf', 'application/pdf'))])])

    expect(neighbour(mixed, 'a1', 1)).toMatchObject({ kind: 'Video' })
    expect(neighbour(mixed, 'a2', 1)).toMatchObject({ kind: 'Pdf' })
  })

  it('goes from a picture to a PDF and back, as they are in one list', () => {
    const mixed = viewerItemsOf([message('m1', [attachment('a1', entry('1.png')), attachment('a2', entry('2.pdf', 'application/pdf')), attachment('a3', entry('3.png'))])])

    expect(neighbour(mixed, 'a1', 1)).toMatchObject({ kind: 'Pdf', source: { id: 'a2' } })
    expect(neighbour(mixed, 'a2', 1)).toMatchObject({ kind: 'Image', source: { id: 'a3' } })
    expect(neighbour(mixed, 'a3', -1)).toMatchObject({ kind: 'Pdf' })
  })

  it('stops at either end and does not wrap round', () => {
    expect(neighbour(items, 'a3', 1)).toBeNull()
    expect(neighbour(items, 'a1', -1)).toBeNull()
  })

  it('has no neighbour for a picture that is not in the list', () => {
    expect(neighbour(items, 'missing', 1)).toBeNull()
    expect(neighbour(items, 'missing', -1)).toBeNull()
  })

  it('says where a picture is, or -1', () => {
    expect(indexOfItem(items, 'a2')).toBe(1)
    expect(indexOfItem(items, 'missing')).toBe(-1)
  })
})
