import { useAuthStore } from '../auth/store/authStore'
import { indexedDbDeviceStore } from './deviceStore'
import { keysApi } from './keysApi'
import { useEncryptionStore } from './store'
import type { DeviceStore, KeysApi } from './types'

// Signing out: this browser stops being a device. Apart from the rest of the feature, and importing no cryptography, because the page
// registers it for every signed-in person and must not carry the encryption code to do so.

interface ReleaseDeps {
  api: Pick<KeysApi, 'removeDevice'>
  store: DeviceStore
}

// Taken off the server (the sessions of the device end with it) and the keys deleted here. The keys are deleted even when the server
// cannot be reached, because the person asked for this browser to forget them.
export async function release(deps: ReleaseDeps, userId: string): Promise<void> {
  const stored = await deps.store.load(userId)
  if (!stored) return

  try {
    await deps.api.removeDevice(stored.deviceId)
  } catch {
    // The device stays in the list until the person removes it there.
  } finally {
    await deps.store.remove(userId)
  }
}

// Run when the person signs out, while the session still works.
export async function releaseDevice(): Promise<void> {
  const userId = useAuthStore.getState().user?.userId
  try {
    if (userId !== undefined) await release({ api: keysApi, store: indexedDbDeviceStore }, userId)
  } finally {
    useEncryptionStore.getState().reset()
  }
}
