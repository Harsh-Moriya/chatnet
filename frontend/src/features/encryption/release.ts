import { useAuthStore } from '../auth/store/authStore'
import { indexedDbDeviceStore } from './deviceStore'
import { indexedDbEpochKeyStore } from './epochKeyStore'
import { indexedDbLocalStore } from '../search/store/indexedDbLocalStore'
import type { LocalStore } from '../search/store/types'
import { keysApi } from './keysApi'
import { indexedDbPinStore } from './pinStore'
import { useEncryptionStore } from './store'
import type { DeviceStore, EpochKeyStore, KeysApi, PinStore } from './types'

// Signing out: this browser stops being a device. Apart from the rest of the feature, and importing no cryptography, because the page
// registers it for every signed-in person and must not carry the encryption code to do so.

interface ReleaseDeps {
  api: Pick<KeysApi, 'removeDevice'>
  store: DeviceStore
  epochKeys: Pick<EpochKeyStore, 'removeAll'>
  pins: Pick<PinStore, 'removeAll'>
  messages: Pick<LocalStore, 'forgetAll'>
}

// What this browser keeps for a person apart from the keys of their device: the keys of their conversations, who they have seen, and the
// messages they have read, opened. Deleted with the keys of the device, because it is opened with them and readable by a script on the
// page just the same.
export async function forgetKept(
  stores: { epochKeys: Pick<EpochKeyStore, 'removeAll'>; pins: Pick<PinStore, 'removeAll'>; messages: Pick<LocalStore, 'forgetAll'> },
  userId: string,
): Promise<void> {
  await Promise.all([stores.epochKeys.removeAll(userId), stores.pins.removeAll(userId), stores.messages.forgetAll(userId)])
}

// Taken off the server (the sessions of the device end with it) and everything kept for the person deleted here: their keys, the keys of
// their conversations, who they have seen and the messages they have read. It is deleted even when the server cannot be reached, because the person asked for this
// browser to forget them.
export async function release(deps: ReleaseDeps, userId: string): Promise<void> {
  try {
    const stored = await deps.store.load(userId)
    if (!stored) return

    try {
      await deps.api.removeDevice(stored.deviceId)
    } catch {
      // The device stays in the list until the person removes it there.
    } finally {
      await deps.store.remove(userId)
    }
  } finally {
    await forgetKept(deps, userId)
  }
}

// Run when the person signs out, while the session still works.
export async function releaseDevice(): Promise<void> {
  const userId = useAuthStore.getState().user?.userId
  try {
    if (userId !== undefined) {
      await release(
        { api: keysApi, store: indexedDbDeviceStore, epochKeys: indexedDbEpochKeyStore, pins: indexedDbPinStore, messages: indexedDbLocalStore },
        userId,
      )
    }
  } finally {
    useEncryptionStore.getState().reset()
  }
}
