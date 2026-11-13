import { useState } from 'react'
import { KeyRound, Link2 } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { unlockWithPassphrase } from '../controller'
import { LinkNewDevicePanel } from './LinkNewDevicePanel'
import { PassphraseForm } from './PassphraseForm'
import { Problem } from './Problem'
import { ResetKeysPanel } from './ResetKeysPanel'
import { useStep } from './useStep'

// For a person whose account has keys that this browser does not hold: open the backup with the passphrase, or ask a device that is
// signed in to hand the keys over.
export function LockedPanel() {
  const { busy, problem, run } = useStep()
  const [linking, setLinking] = useState(false)
  const [resetting, setResetting] = useState(false)

  if (linking) return <LinkNewDevicePanel onCancel={() => setLinking(false)} />
  if (resetting) return <ResetKeysPanel onCancel={() => setResetting(false)} />

  return (
    <div className="space-y-3">
      <p className="text-xs text-slate-500">
        Your encryption keys are not on this browser. Enter your recovery passphrase to restore them, or link this browser from another
        device where you are signed in.
      </p>
      <Problem message={problem} />
      <PassphraseForm
        idPrefix="unlock"
        label="Recovery passphrase"
        submitLabel="Restore keys"
        busy={busy}
        onSubmit={(passphrase) => void run(() => unlockWithPassphrase(passphrase))}
      />
      <Button variant="ghost" size="sm" onClick={() => setLinking(true)}>
        <Link2 className="h-3.5 w-3.5" />
        Link from another device instead
      </Button>
      <Button variant="ghost" size="sm" onClick={() => setResetting(true)}>
        <KeyRound className="h-3.5 w-3.5" />
        I have no other device and lost my passphrase
      </Button>
    </div>
  )
}
