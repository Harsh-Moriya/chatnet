import { useEffect, type ReactNode } from 'react'
import { AlertCircle, LockKeyhole } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { SessionLoading } from '../../../components/SessionLoading'
import { session } from '../../auth/session'
import { loadEncryption } from '../controller'
import { type GateScreen, gateScreen } from '../gate'
import { useEncryptionSync } from '../hooks/useEncryptionSync'
import { useEncryptionStore } from '../store'
import { LockedPanel } from './LockedPanel'
import { SetUpPanel } from './SetUpPanel'

// Between signing in and the chat: a person whose browser does not hold their keys sets them up, restores them or links this browser
// before anything else, because every conversation is sealed to the keys. Once this browser holds them the chat stays, however the state
// of the keys is asked about again later (opening the profile checks it, which passes through "loading").
export function EncryptionGate({ children }: { children: ReactNode }) {
  useEncryptionSync()

  const status = useEncryptionStore((state) => state.status)
  const hasKeys = useEncryptionStore((state) => state.secrets !== null)

  useEffect(() => {
    if (useEncryptionStore.getState().status === 'idle') void loadEncryption()
  }, [])

  const screen = gateScreen(status, hasKeys)
  if (screen === 'chat') return <>{children}</>
  if (screen === 'loading') return <SessionLoading />

  return (
    <main className="flex min-h-screen items-center justify-center bg-slate-50 px-4 py-8">
      <div className="w-full max-w-md space-y-4 rounded-lg border border-line bg-white p-6">
        <GateContent screen={screen} />
        <div className="border-t border-line pt-3">
          <Button variant="ghost" size="sm" onClick={() => void session.signOut()}>
            Sign out
          </Button>
        </div>
      </div>
    </main>
  )
}

function Heading({ children }: { children: ReactNode }) {
  return (
    <div className="flex items-center gap-2">
      <LockKeyhole className="h-5 w-5 text-blue-600" aria-hidden="true" />
      <h1 className="text-lg font-semibold text-slate-900">{children}</h1>
    </div>
  )
}

function GateContent({ screen }: { screen: Exclude<GateScreen, 'chat' | 'loading'> }) {
  const removedElsewhere = useEncryptionStore((state) => state.removedElsewhere)

  if (screen === 'unavailable') {
    return (
      <>
        <Heading>This browser cannot keep encryption keys</Heading>
        <p className="text-sm text-slate-500">
          Your messages are encrypted, and this browser cannot hold the keys that open them. It may be a private window, or it does not
          support the algorithms the app uses. Open the app in a current version of Chrome, Edge, Firefox or Safari.
        </p>
      </>
    )
  }

  if (screen === 'error') {
    return (
      <>
        <Heading>Checking your encryption keys</Heading>
        <div role="alert" className="flex items-start gap-2 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-600">
          <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
          <span>Your keys could not be checked. Check your connection and try again.</span>
        </div>
        <Button size="sm" onClick={() => void loadEncryption()}>
          Try again
        </Button>
      </>
    )
  }

  if (screen === 'locked') {
    return (
      <>
        <Heading>Unlock your messages</Heading>
        {removedElsewhere && (
          <div role="status" className="flex items-start justify-between gap-2 rounded-lg border border-line bg-slate-50 px-3 py-2 text-xs text-slate-700">
            <span>This browser was removed from your devices, so its keys were deleted.</span>
            <Button variant="ghost" size="sm" onClick={() => useEncryptionStore.getState().acknowledgeRemoval()}>
              Dismiss
            </Button>
          </div>
        )}
        <LockedPanel />
      </>
    )
  }

  return (
    <>
      <Heading>Set up encryption</Heading>
      <SetUpPanel />
    </>
  )
}
