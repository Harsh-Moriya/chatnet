import { PINS, asPromise, startingWith, transaction } from './idb'
import type { Pin, PinStore } from './types'

// The identity key each person was first seen with, kept in this browser (see idb.ts). One record per owner and contact.
export const indexedDbPinStore: PinStore = {
  load: (ownerId, contactId) =>
    transaction([PINS], 'readonly', async (tx) => {
      const found = (await asPromise(tx.objectStore(PINS).get([ownerId, contactId]))) as Pin | undefined
      return found ?? null
    }),

  save: (pin) =>
    transaction([PINS], 'readwrite', async (tx) => {
      await asPromise(tx.objectStore(PINS).put(pin))
    }),

  removeAll: (ownerId) =>
    transaction([PINS], 'readwrite', async (tx) => {
      await asPromise(tx.objectStore(PINS).delete(startingWith(ownerId)))
    }),
}
