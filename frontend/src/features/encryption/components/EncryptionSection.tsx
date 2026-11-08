import { useEffect } from 'react'
import { AlertCircle } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { loadEncryption } from '../controller'
import { useEncryptionStore } from '../store'
import { ApproveDevicePanel } from './ApproveDevicePanel'
import { ChangePassphrasePanel } from './ChangePassphrasePanel'
import { DevicesPanel } from './DevicesPanel'
import { LockedPanel } from './LockedPanel'
import { SetUpPanel } from './SetUpPanel'

// The encryption keys of the signed-in person, in the profile: set them up, restore them on a new browser, hand them to another
// device, change the passphrase that protects the backup. What it shows follows from what this browser and the server hold.
export function EncryptionSection() {
  const status = useEncryptionStore((state) => state.status)
  const removedElsewhere = useEncryptionStore((state) => state.removedElsewhere)

  useEffect(() => {
    void loadEncryption()
  }, [])

  return (
    <section aria-labelledby="encryption-heading" className="space-y-3 border-t border-line pt-4">
      <h3 id="encryption-heading" className="text-sm font-medium text-slate-700">
        Encryption keys
      </h3>

      {removedElsewhere && (
        <div role="status" className="flex items-start justify-between gap-2 rounded-lg border border-line bg-slate-50 px-3 py-2 text-xs text-slate-700">
          <span>This browser was removed from your devices, so its keys were deleted.</span>
          <Button variant="ghost" size="sm" onClick={() => useEncryptionStore.getState().acknowledgeRemoval()}>
            Dismiss
          </Button>
        </div>
      )}

      {(status === 'idle' || status === 'loading') && <p className="text-xs text-slate-500">Checking your keys...</p>}

      {status === 'unavailable' && (
        <p className="text-xs text-slate-500">
          This browser cannot keep encryption keys. It may be a private window, or it does not support the algorithms the app uses.
        </p>
      )}

      {status === 'error' && (
        <div className="space-y-2">
          <div role="alert" className="flex items-start gap-2 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-600">
            <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
            <span>Your keys could not be checked. Check your connection and try again.</span>
          </div>
          <Button variant="ghost" size="sm" onClick={() => void loadEncryption()}>
            Try again
          </Button>
        </div>
      )}

      {status === 'notSetUp' && <SetUpPanel />}

      {status === 'locked' && <LockedPanel />}

      {status === 'ready' && (
        <div className="space-y-4">
          <p className="text-xs text-slate-500">Your keys are on this browser. Your recovery passphrase restores them on another.</p>
          <DevicesPanel />
          <ApproveDevicePanel />
          <ChangePassphrasePanel />
        </div>
      )}
    </section>
  )
}
