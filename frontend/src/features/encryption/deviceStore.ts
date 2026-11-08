import type { DeviceStore, StoredDevice } from './types'

// The keys of a person, kept in this browser. IndexedDB holds them because a page can read nothing else that survives a reload
// and can keep binary data. A script on the page can read them too (the backup and device linking need the keys in a form that
// can be exported), which is why signing out deletes them.
//
// One record per person, so two people who use one browser each have their own, and signing out one leaves the other's.

const DATABASE = 'chatnet-e2ee'
const VERSION = 1
const DEVICES = 'devices'
const META = 'meta'
const LAST_USER = 'lastUserId'

// Opens the database, making the stores the first time. A rejected promise is how a browser that has no IndexedDB, or blocks it
// (some private windows), is told apart from one that has no keys saved.
function open(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    if (typeof indexedDB === 'undefined') {
      reject(new Error('IndexedDB is not available'))
      return
    }

    const request = indexedDB.open(DATABASE, VERSION)
    request.onupgradeneeded = () => {
      request.result.createObjectStore(DEVICES, { keyPath: 'userId' })
      request.result.createObjectStore(META)
    }
    request.onsuccess = () => resolve(request.result)
    request.onerror = () => reject(request.error ?? new Error('IndexedDB could not be opened'))
    request.onblocked = () => reject(new Error('IndexedDB is blocked'))
  })
}

// Runs the work in one transaction and resolves when the transaction has committed, not merely when the request returned.
async function transaction<T>(stores: string[], mode: IDBTransactionMode, work: (tx: IDBTransaction) => Promise<T>): Promise<T> {
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

function asPromise<T>(request: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result)
    request.onerror = () => reject(request.error ?? new Error('IndexedDB request failed'))
  })
}

export const indexedDbDeviceStore: DeviceStore = {
  load: (userId) =>
    transaction([DEVICES], 'readonly', async (tx) => {
      const found = (await asPromise(tx.objectStore(DEVICES).get(userId))) as StoredDevice | undefined
      return found ?? null
    }),

  save: (device) =>
    transaction([DEVICES, META], 'readwrite', async (tx) => {
      await asPromise(tx.objectStore(DEVICES).put(device))
      await asPromise(tx.objectStore(META).put(device.userId, LAST_USER))
    }),

  // The pointer to the last person is left as it is: once the record it points to is gone it finds nothing.
  remove: (userId) =>
    transaction([DEVICES], 'readwrite', async (tx) => {
      await asPromise(tx.objectStore(DEVICES).delete(userId))
    }),

  lastDeviceId: () =>
    transaction([DEVICES, META], 'readonly', async (tx) => {
      const last = (await asPromise(tx.objectStore(META).get(LAST_USER))) as string | undefined
      if (last === undefined) return null
      const found = (await asPromise(tx.objectStore(DEVICES).get(last))) as StoredDevice | undefined
      return found?.deviceId ?? null
    }),
}

// Whether this browser can keep keys at all: IndexedDB opens, and WebCrypto does the algorithms the formats use.
export async function canHoldKeys(): Promise<boolean> {
  try {
    const db = await open()
    db.close()
    await crypto.subtle.generateKey({ name: 'X25519' }, false, ['deriveBits'])
    await crypto.subtle.generateKey({ name: 'Ed25519' }, false, ['sign'])
    return true
  } catch {
    return false
  }
}
