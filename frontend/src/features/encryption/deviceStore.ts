import { DEVICES, META, asPromise, open, transaction } from './idb'
import type { DeviceStore, StoredDevice } from './types'

// The keys of a person, kept in this browser. IndexedDB holds them because a page can read nothing else that survives a reload
// and can keep binary data. A script on the page can read them too (the backup and device linking need the keys in a form that
// can be exported), which is why signing out deletes them.
//
// One record per person, so two people who use one browser each have their own, and signing out one leaves the other's.

const LAST_USER = 'lastUserId'

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
