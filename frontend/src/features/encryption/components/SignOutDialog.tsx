import { useEffect, useState } from 'react'
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
  // Unknown until the server has answered, and when it cannot be reached: the warning for the worst case is shown then.
  const [onlyDevice, setOnlyDevice] = useState<boolean | null>(null)

  useEffect(() => {
    let current = true
    keysApi.listDevices().then(
      (devices) => {
        if (current) setOnlyDevice(devices.length <= 1)
      },
      () => undefined,
    )
    return () => {
      current = false
    }
  }, [])

  return (
    <div className="space-y-4 p-5">
      <p className="text-sm text-slate-700">
        Signing out deletes your encryption keys from this browser. To read your messages here again you will need your recovery
        passphrase, or another device where you are signed in.
      </p>
      {onlyDevice !== false && (
        <p role="alert" className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
          {onlyDevice === true ? 'This is the only device with your keys. ' : 'This may be the only device with your keys. '}
          Without your recovery passphrase, your messages could not be read again.
        </p>
      )}
      <div className="flex justify-end gap-2">
        <Button variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
        <Button onClick={() => void session.signOut()}>Sign out</Button>
      </div>
    </div>
  )
}
