import type { EncryptionStatus } from './store'

// What stands between a signed-in person and the chat.
export type GateScreen = 'chat' | 'loading' | 'unavailable' | 'error' | 'locked' | 'outdated' | 'setUp'

// A browser that holds the person's keys goes straight to the chat, whatever the state of checking them is: opening the profile asks
// again, which passes through "loading", and a failure to ask does not take away keys that are in memory. Without keys, the screen
// follows from what was found out: nothing yet, a browser that cannot keep them, a failure to ask, keys that exist but are not here, or
// no keys at all.
export function gateScreen(status: EncryptionStatus, hasKeys: boolean): GateScreen {
  if (hasKeys) return 'chat'

  switch (status) {
    case 'idle':
    case 'loading':
      return 'loading'
    case 'unavailable':
      return 'unavailable'
    case 'error':
      return 'error'
    case 'locked':
      return 'locked'
    case 'outdated':
      return 'outdated'
    case 'notSetUp':
    case 'ready':
      // 'ready' without keys cannot be reached: the store sets both together. If it ever were, asking the person to set up is the safe side.
      return 'setUp'
  }
}
