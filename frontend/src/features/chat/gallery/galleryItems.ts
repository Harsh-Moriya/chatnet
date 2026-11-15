import type { Message } from '../types'
import { type ViewerItem, viewerItemsOf } from '../viewer/viewerItems'

// The files of the gallery: what the viewer can show in the pages of messages loaded so far, newest message first. A message the chat
// already knows is deleted is left out even though the page that held it was read before the deletion.
export function galleryItemsOf(pages: readonly (readonly Message[])[], deletedIds: ReadonlySet<string>): ViewerItem[] {
  return viewerItemsOf(pages.flat().filter((message) => !deletedIds.has(message.id)))
}

// The ids of the messages the chat holds as deleted.
export function deletedIdsOf(messages: readonly Message[] | undefined): Set<string> {
  return new Set((messages ?? []).filter((message) => message.isDeleted).map((message) => message.id))
}
