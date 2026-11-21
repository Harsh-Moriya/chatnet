import { useEffect } from 'react'
import { Button } from '../../../components/ui/Button'
import { loadEncryption } from '../controller'
import { useEncryptionStore } from '../store'
import { ApproveDevicePanel } from './ApproveDevicePanel'
import { ChangePassphrasePanel } from './ChangePassphrasePanel'
import { DevicesPanel } from './DevicesPanel'
import { Alert } from '../../../components/ui/Alert'

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
      <h3 id="encryption-heading" className="text-sm font-medium text-fg-soft">
        Encryption keys
      </h3>

      {(status === 'idle' || status === 'loading') && <p className="text-xs text-fg-muted">Checking your keys...</p>}

      {status === 'error' && (
        <div className="space-y-2">
          <Alert role="alert" size="sm">Your keys could not be checked. Check your connection and try again.</Alert>
          <Button variant="ghost" size="sm" onClick={() => void loadEncryption()}>
            Try again
          </Button>
        </div>
      )}

      {status === 'ready' && (
        <div className="space-y-4">
          <p className="text-xs text-fg-muted">Your keys are on this browser. Your recovery passphrase restores them on another.</p>
          <DevicesPanel />
          <ApproveDevicePanel />
          <ChangePassphrasePanel />
        </div>
      )}
    </section>
  )
}
