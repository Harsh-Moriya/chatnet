import { useAuthStore } from '../auth/store/authStore'
import { session } from '../auth/session'
import { canHoldKeys, indexedDbDeviceStore } from './deviceStore'
import { describeBrowser } from './deviceName'
import {
  type FlowDeps,
  type LinkAttempt,
  approveLinkRequest,
  changePassphrase as changePassphraseFlow,
  findLinkRequest,
  loadState,
  setUp,
  startLink,
  unlock,
} from './flows'
import { keysApi } from './keysApi'
import { useEncryptionStore } from './store'
import type { LinkRequestInfo } from './types'

// Joins the flows (flows.ts) to the real server, this browser's store, the signed-in person and the screens. The screens call these;
// the rules are in the flows.

// Waits, and stops waiting when the signal is aborted.
function wait(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(signal.reason)
      return
    }

    const timer = setTimeout(() => {
      signal?.removeEventListener('abort', onAbort)
      resolve()
    }, ms)

    function onAbort() {
      clearTimeout(timer)
      reject(signal?.reason)
    }

    signal?.addEventListener('abort', onAbort, { once: true })
  })
}

function deps(): FlowDeps {
  return {
    api: keysApi,
    store: indexedDbDeviceStore,
    device: { kind: 'Web', name: describeBrowser(navigator.userAgent) },
    wait,
  }
}

function currentUserId(): string {
  const userId = useAuthStore.getState().user?.userId
  if (userId === undefined) throw new Error('Nobody is signed in.')
  return userId
}

// Once this browser is a device, the session it is signed in on is tied to it: a renewal now carries the device id (see the provider
// in session.ts), and the server remembers it, so removing the device ends this session. Failing to renew is no failure of setting up.
async function tieSessionToDevice(): Promise<void> {
  await session.refresh().catch(() => undefined)
}

// Asks what this browser has and what the server says, and puts the answer where the screens read it.
export async function loadEncryption(): Promise<void> {
  const store = useEncryptionStore.getState()
  store.setStatus('loading')

  if (!(await canHoldKeys())) {
    store.setStatus('unavailable')
    return
  }

  try {
    store.applyLoaded(await loadState(deps(), currentUserId()))
  } catch {
    store.setStatus('error')
  }
}

export async function setUpEncryption(passphrase: string): Promise<void> {
  useEncryptionStore.getState().applyReady(await setUp(deps(), currentUserId(), passphrase))
  await tieSessionToDevice()
}

export async function unlockWithPassphrase(passphrase: string): Promise<void> {
  useEncryptionStore.getState().applyReady(await unlock(deps(), currentUserId(), passphrase))
  await tieSessionToDevice()
}

// Opens a request to be linked and waits for another device to approve it; the keys arrive in the store when it does.
export async function linkThisDevice(signal: AbortSignal): Promise<LinkAttempt> {
  const attempt = await startLink(deps(), currentUserId(), signal)

  void attempt.result.then(
    async (ready) => {
      useEncryptionStore.getState().applyReady(ready)
      await tieSessionToDevice()
    },
    () => undefined,
  )

  return attempt
}

export async function findDeviceToLink(code: string): Promise<LinkRequestInfo> {
  return findLinkRequest(deps(), code)
}

export async function approveDevice(request: LinkRequestInfo): Promise<void> {
  const { secrets } = useEncryptionStore.getState()
  if (secrets === null) throw new Error('This browser does not hold the keys.')
  await approveLinkRequest(deps(), currentUserId(), secrets, request)
}

export async function changePassphrase(passphrase: string): Promise<void> {
  const { secrets } = useEncryptionStore.getState()
  if (secrets === null) throw new Error('This browser does not hold the keys.')
  await changePassphraseFlow(deps(), currentUserId(), secrets, passphrase)
}

export function listDevices() {
  return keysApi.listDevices()
}

