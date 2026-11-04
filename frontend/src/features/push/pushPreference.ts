// Whether a person turned alerts on in this browser. It is kept here, in the browser, and per person: the alerts are one browser's
// to show, and a browser is shared by whoever signs in on it. Nothing about it is on the server; the server only knows which
// browsers are registered.

export type PreferenceStorage = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>

const keyFor = (userId: string) => `chatnet-push:${userId}`

// Storage can be unavailable (private windows, blocked site data). Then alerts are simply off, and turning them on lasts until the
// page is closed at most.
function browserStorage(): PreferenceStorage | null {
  try {
    return localStorage
  } catch {
    return null
  }
}

export function wantsAlerts(userId: string, storage: PreferenceStorage | null = browserStorage()): boolean {
  try {
    return storage?.getItem(keyFor(userId)) === 'on'
  } catch {
    return false
  }
}

export function rememberWantsAlerts(
  userId: string,
  wants: boolean,
  storage: PreferenceStorage | null = browserStorage(),
): void {
  try {
    if (wants) storage?.setItem(keyFor(userId), 'on')
    else storage?.removeItem(keyFor(userId))
  } catch {
    // Nothing to do: the page works the same, and the choice is asked for again after a reload.
  }
}
