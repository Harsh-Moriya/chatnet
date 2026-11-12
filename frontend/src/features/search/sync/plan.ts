import type { ReadPoint } from '../store/types'

// What the local store needs from the server, conversation by conversation: as plain functions so it can be tested without a browser.

// A conversation as the list says it is: its id and the number of its latest change.
export interface Listed {
  id: string
  changeSeq: number
}

export interface Work {
  id: string
  // The conversation's number as the list said it, which is where the feed continues from after the history has been read.
  changeSeq: number
  // Its history has not been read yet (or was only part read).
  history: boolean
}

// The conversations that need something, in the order the list gives them (the most active first, so what the person is likeliest to
// search is there first). A conversation whose history has been read and whose cursor is where the list says the conversation is has
// nothing to ask for. One whose cursor is not where the list says (a cursor beyond it means the server went back to an older state) asks
// the feed, which says which it is.
export function planSync(listed: readonly Listed[], points: readonly ReadPoint[]): Work[] {
  const byConversation = new Map(points.map((point) => [point.conversationId, point]))
  const work: Work[] = []

  for (const { id, changeSeq } of listed) {
    const point = byConversation.get(id)

    if (!point?.historyRead) work.push({ id, changeSeq, history: true })
    else if (point.cursor !== changeSeq) work.push({ id, changeSeq, history: false })
  }

  return work
}

// A short text that changes when any conversation's number does, to run the sync when the list moves and not otherwise.
export function signatureOf(conversations: Record<string, { changeSeq: number }>): string {
  return Object.entries(conversations)
    .map(([id, conversation]) => `${id}:${conversation.changeSeq}`)
    .sort()
    .join(',')
}
