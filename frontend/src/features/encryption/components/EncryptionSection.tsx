import { useEffect } from 'react'
import { AlertCircle } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { loadEncryption } from '../controller'
import { useEncryptionStore } from '../store'
import { ApproveDevicePanel } from './ApproveDevicePanel'
import { ChangePassphrasePanel } from './ChangePassphrasePanel'
import { DevicesPanel } from './DevicesPanel'

// The encryption keys of the signed-in person, in the profile: hand them to another device, see and remove devices, change the
// passphrase that protects the backup. This browser holds the keys by the time the profile can be opened (the gate before the chat
// sees to that), so what it shows follows from checking them again.
export function EncryptionSection() {
  const status = useEncryptionStore((state) => state.status)

  useEffect(() => {
    void loadEncryption()
  }, [])

  return (
    <section aria-labelledby="encryption-heading" className="space-y-3 border-t border-line pt-4">
      <h3 id="encryption-heading" className="text-sm font-medium text-slate-700">
        Encryption keys
      </h3>

      {(status === 'idle' || status === 'loading') && <p className="text-xs text-slate-500">Checking your keys...</p>}

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
