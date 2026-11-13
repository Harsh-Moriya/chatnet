import { catchUpWithPassphrase } from '../controller'
import { PassphraseForm } from './PassphraseForm'
import { Problem } from './Problem'
import { useStep } from './useStep'

// For a browser that is a device of the person but holds older keys than the account now has: a device was removed as lost from another
// one, which made a new generation of the account key, and its private half is only in the backup. The passphrase opens it.
export function OutdatedPanel() {
  const { busy, problem, run } = useStep()

  return (
    <div className="space-y-3">
      <p className="text-xs text-slate-500">
        Your encryption keys were renewed on another device, because a device was removed from your account. Enter your recovery passphrase
        to bring the new keys to this browser. Nothing is lost: you stay signed in as this device.
      </p>
      <Problem message={problem} />
      <PassphraseForm
        idPrefix="catchup"
        label="Recovery passphrase"
        submitLabel="Get the new keys"
        busy={busy}
        onSubmit={(passphrase) => void run(() => catchUpWithPassphrase(passphrase))}
      />
    </div>
  )
}
