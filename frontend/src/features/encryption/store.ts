import { create } from 'zustand'
import type { AccountSecrets } from '../../lib/crypto'
import type { EncryptionState, LoadedState, Ready } from './flows'

// What this browser knows about the keys of the signed-in person, for the screens. The secrets are held in memory while the person
// is signed in, because approving another device and changing the passphrase need them; the copy that survives a reload is the one
// in IndexedDB (deviceStore.ts). Reset when the person signs out.

export type EncryptionStatus =
  // Nothing asked yet.
  | 'idle'
  | 'loading'
  // This browser cannot hold keys (no IndexedDB, or WebCrypto lacks the algorithms).
  | 'unavailable'
  // The server or the store could not be asked; the screen offers to try again.
  | 'error'
  | EncryptionState['phase']

interface EncryptionStore {
  status: EncryptionStatus
  deviceId: string | null
  secrets: AccountSecrets | null
  // The keys of this browser were deleted because the person removed it from their devices elsewhere. Shown once.
  removedElsewhere: boolean
  setStatus: (status: EncryptionStatus) => void
  applyLoaded: (loaded: LoadedState) => void
  applyReady: (ready: Ready) => void
  acknowledgeRemoval: () => void
  reset: () => void
}

const empty = { status: 'idle', deviceId: null, secrets: null, removedElsewhere: false } as const

export const useEncryptionStore = create<EncryptionStore>()((set) => ({
  ...empty,
  setStatus: (status) => set({ status }),
  applyLoaded: ({ state, removedElsewhere }) =>
    set(
      state.phase === 'ready'
        ? { status: 'ready', deviceId: state.deviceId, secrets: state.secrets, removedElsewhere }
        : // An outdated browser is still a device, so it keeps the id it was given, and its keys are left out of memory until the
          // passphrase brings the newer ones.
          { status: state.phase, deviceId: state.phase === 'outdated' ? state.deviceId : null, secrets: null, removedElsewhere },
    ),
  applyReady: (ready) => set({ status: 'ready', deviceId: ready.deviceId, secrets: ready.secrets, removedElsewhere: false }),
  acknowledgeRemoval: () => set({ removedElsewhere: false }),
  reset: () => set({ ...empty }),
}))
