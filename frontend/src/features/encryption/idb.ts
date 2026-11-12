// The one IndexedDB database this feature keeps in the browser, and the plumbing every store in it shares. The stores hold: the keys
// of the person who signed in on this device, the keys of the conversations they are in, the identities of the people they talk to, and
// the messages they have read, opened, with how far each conversation has been read (search runs over those). A script on the page can
// read all of it (the keys have to be usable, and so is the text), which is why signing out deletes it.

export const DATABASE = 'chatnet-e2ee'
export const VERSION = 3

export const DEVICES = 'devices'
export const META = 'meta'
export const EPOCH_KEYS = 'epochKeys'
export const PINS = 'pins'
export const MESSAGES = 'messages'
export const READ_POINTS = 'readPoints'

// The index of the messages store that lists a person's messages by time, for reading them newest first.
export const MESSAGES_BY_TIME = 'byTime'

// Opens the database, making the stores that are missing: all of them the first time, and the ones added later for a browser that
// has an older version. A rejected promise is how a browser that has no IndexedDB, or blocks it (some private windows), is told
// apart from one that has nothing saved.
export function open(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    if (typeof indexedDB === 'undefined') {
      reject(new Error('IndexedDB is not available'))
      return
    }

    const request = indexedDB.open(DATABASE, VERSION)
    request.onupgradeneeded = () => {
      const db = request.result
      if (!db.objectStoreNames.contains(DEVICES)) db.createObjectStore(DEVICES, { keyPath: 'userId' })
      if (!db.objectStoreNames.contains(META)) db.createObjectStore(META)
      if (!db.objectStoreNames.contains(EPOCH_KEYS)) db.createObjectStore(EPOCH_KEYS, { keyPath: ['userId', 'conversationId', 'epoch'] })
      if (!db.objectStoreNames.contains(PINS)) db.createObjectStore(PINS, { keyPath: ['ownerId', 'contactId'] })
      if (!db.objectStoreNames.contains(MESSAGES)) {
        const messages = db.createObjectStore(MESSAGES, { keyPath: ['userId', 'conversationId', 'id'] })
        messages.createIndex(MESSAGES_BY_TIME, ['userId', 'at', 'id'])
      }
      if (!db.objectStoreNames.contains(READ_POINTS)) db.createObjectStore(READ_POINTS, { keyPath: ['userId', 'conversationId'] })
    }
    request.onsuccess = () => resolve(request.result)
    request.onerror = () => reject(request.error ?? new Error('IndexedDB could not be opened'))
    request.onblocked = () => reject(new Error('IndexedDB is blocked'))
  })
}

// Runs the work in one transaction and resolves when the transaction has committed, not merely when the request returned.
export async function transaction<T>(stores: string[], mode: IDBTransactionMode, work: (tx: IDBTransaction) => Promise<T>): Promise<T> {
  const db = await open()
  try {
    const tx = db.transaction(stores, mode)
    const done = new Promise<void>((resolve, reject) => {
      tx.oncomplete = () => resolve()
      tx.onerror = () => reject(tx.error ?? new Error('IndexedDB transaction failed'))
      tx.onabort = () => reject(tx.error ?? new Error('IndexedDB transaction aborted'))
    })
    const result = await work(tx)
    await done
    return result
  } finally {
    db.close()
  }
}

export function asPromise<T>(request: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result)
    request.onerror = () => reject(request.error ?? new Error('IndexedDB request failed'))
  })
}

// Every key of a store that starts with these parts. Array keys sort element by element, and an array sorts after any string or
// number, so [..parts, []] is above every key that begins with the parts and [...parts] is the first of them.
export function startingWith(...parts: IDBValidKey[]): IDBKeyRange {
  return IDBKeyRange.bound(parts, [...parts, []])
}
