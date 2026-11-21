import { useEffect, useState } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { session } from '../../auth/session'
import { keysApi } from '../keysApi'

interface SignOutDialogProps {
  open: boolean
  onClose: () => void
}

// Asked before signing out of a browser that holds the keys: signing out deletes them here. When this is the only device, the
// recovery passphrase is the only way back, and the dialog says so.
export function SignOutDialog({ open, onClose }: SignOutDialogProps) {
  return (
    <Dialog open={open} onClose={onClose} title="Sign out">
      <Asking onCancel={onClose} />
    </Dialog>
  )
}

function Asking({ onCancel }: { onCancel: () => void }) {
  // `checking` until the server has answered, when nothing is said (a warning shown for a moment and then taken back reads as a glitch);
  // `unknown` when it cannot be reached, which gets the warning for the worst case.
  const [onlyDevice, setOnlyDevice] = useState<boolean | 'checking' | 'unknown'>('checking')

  useEffect(() => {
    let current = true
    keysApi.listDevices().then(
      (devices) => {
        if (current) setOnlyDevice(devices.length <= 1)
      },
      () => {
        if (current) setOnlyDevice('unknown')
      },
    )
    return () => {
      current = false
    }
  }, [])

  return (
    <div className="space-y-4 p-5">
      <p className="text-sm text-fg-soft">
        Signing out deletes your encryption keys from this browser. To read your messages here again you will need your recovery
        passphrase, or another device where you are signed in.
      </p>
      {(onlyDevice === true || onlyDevice === 'unknown') && (
        <Alert kind="warning" role="alert">
          {onlyDevice === true ? 'This is the only device with your keys. ' : 'This may be the only device with your keys. '}
          Without your recovery passphrase, your messages could not be read again.
        </Alert>
      )}
      <div className="flex justify-end gap-2">
        <Button variant="secondary" onClick={onCancel}>
          Cancel
        </Button>
        <Button onClick={() => void session.signOut()}>Sign out</Button>
      </div>
    </div>
  )
}
