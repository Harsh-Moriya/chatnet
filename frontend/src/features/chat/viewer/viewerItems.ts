import { type AttachmentKind, cleanFileName, kindOf } from '../files/fileNames'
import type { FileSource } from '../files/fileObjects'
import type { Message } from '../types'

// The files of a conversation the viewer can show, and the way from one to the next.

// The kinds of file the viewer has a renderer for.
export type ViewerKind = Extract<AttachmentKind, 'Image' | 'Pdf'>

export interface ViewerItem {
  kind: ViewerKind
  source: FileSource
  name: string
  size: number
  senderName: string
  sentAt: string
}

// The pictures and the PDFs of the messages, in the order the messages are in (the store keeps them oldest first). A file is only listed when
// the sealed message vouches for it, and what the sender says it is is one the app shows (so not SVG): the same test the message does before
// it draws it. Nothing of a deleted message is listed.
export function viewerItemsOf(messages: readonly Message[]): ViewerItem[] {
  return messages.flatMap((message) => {
    if (message.isDeleted) return []

    return message.attachments.flatMap((attachment) => {
      const file = attachment.file
      const kind = file === null ? null : kindOf(file)
      if (file === null || (kind !== 'Image' && kind !== 'Pdf')) return []

      return [
        {
          kind,
          source: { id: attachment.id, conversationId: message.conversationId, messageId: message.id, url: attachment.url, entry: file },
          name: cleanFileName(file.name),
          size: file.size,
          senderName: message.senderDisplayName,
          sentAt: message.createdAt,
        },
      ]
    })
  })
}

export function indexOfItem(items: readonly ViewerItem[], id: string): number {
  return items.findIndex((item) => item.source.id === id)
}

// The item before (-1) or after (1) the one with this id, or null at either end or when the id is not in the list. It does not wrap round.
export function neighbour(items: readonly ViewerItem[], id: string, step: 1 | -1): ViewerItem | null {
  const index = indexOfItem(items, id)
  if (index < 0) return null

  return items[index + step] ?? null
}
