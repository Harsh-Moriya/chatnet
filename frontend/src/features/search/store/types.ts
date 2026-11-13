// What this device keeps of the messages the person has read, so they can be searched. A message here is the opened one: the text is
// plain (see features/encryption/idb.ts for what that means), and the sealed blob is kept only for a message that could not be opened
// yet, to open it again when its key arrives.

// A file of a message, by the name its sender gave it, and that name's words.
export interface StoredFile {
  name: string
  words: string[]
}

interface StoredBase {
  userId: string
  conversationId: string
  id: string
  senderId: string
  // The name the sender had when the message was last downloaded.
  senderName: string
  createdAt: string
  // createdAt as milliseconds, which is what the messages are listed by (an ISO string does not sort when its fraction is cut short).
  at: number
  editCount: number
  // The number of the latest change to the message in its conversation (see the changes feed). A copy never replaces one with a
  // higher number, so a late copy cannot bring back what was edited or deleted since.
  changeSeq: number
}

export interface StoredText extends StoredBase {
  state: 'text'
  text: string
  // The words of the text in order, which is what a search tests.
  words: string[]
  files: StoredFile[]
}

// Could not be opened here yet (no key, or it did not check out). Kept as it was sealed, with the size of each file it carried: the names
// of the files are inside the sealed message, and are only known for the files whose stored size agrees with it.
export interface StoredSealed extends StoredBase {
  state: 'sealed'
  sealed: { ciphertext: string; keyEpoch: number; clientMessageId: string; files: { id: string; sizeBytes: number }[] }
}

// Deleted. Kept without text so that an older copy of the message that arrives late is known to be older.
export interface StoredDeleted extends StoredBase {
  state: 'deleted'
}

export type StoredMessage = StoredText | StoredSealed | StoredDeleted

// Where a scan stopped: the message it visited last. The next scan continues just below it.
export interface ScanPosition {
  at: number
  id: string
}

// How far a conversation has been read into the store.
export interface ReadPoint {
  userId: string
  conversationId: string
  // The number the changes feed is asked from next. Null until the history has been read.
  cursor: number | null
  // The conversation's number when its history was first read: the feed continues from it, so what changed while the history was
  // being read is read again.
  startSeq: number | null
  // The oldest message of the history read so far, to continue from when reading the history was interrupted.
  oldestId: string | null
  historyRead: boolean
}

// What a page read adds to a conversation's read point.
export interface ReadPointChange {
  cursor?: number | null
  startSeq?: number | null
  oldestId?: string | null
  historyRead?: boolean
}

export interface LocalStore {
  // Makes the store ready for a person: a store written in another format is emptied, to be read again from the server.
  prepare: (userId: string) => Promise<void>

  // Keeps the messages (never an older copy over a newer) and moves the conversation's read point, in one transaction, so what the
  // read point says has been read is in the store.
  apply: (userId: string, conversationId: string, messages: StoredMessage[], change?: ReadPointChange) => Promise<void>

  readPoint: (userId: string, conversationId: string) => Promise<ReadPoint | null>
  readPoints: (userId: string) => Promise<ReadPoint[]>

  // The messages of a conversation that could not be opened.
  sealed: (userId: string, conversationId: string) => Promise<StoredSealed[]>

  // Visits the person's messages newest first, from just below `after`, until `visit` says stop or there are no more. Returns where
  // it stopped and whether it ran out.
  scan: (
    userId: string,
    after: ScanPosition | null,
    visit: (message: StoredMessage) => boolean,
  ) => Promise<{ last: ScanPosition | null; exhausted: boolean }>

  // Forgets one conversation (to read it again), or everything of a person (signing out).
  forgetConversation: (userId: string, conversationId: string) => Promise<void>
  forgetAll: (userId: string) => Promise<void>
}
