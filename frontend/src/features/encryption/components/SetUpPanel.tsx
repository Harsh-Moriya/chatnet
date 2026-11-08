import { setUpEncryption } from '../controller'
import { PassphraseForm } from './PassphraseForm'
import { Problem } from './Problem'
import { useStep } from './useStep'

// For a person whose account has no keys yet: choose the recovery passphrase, and the keys are made in this browser.
export function SetUpPanel() {
  const { busy, problem, run } = useStep()

  return (
    <div className="space-y-3">
      <p className="text-xs text-slate-500">
        Your keys are made in this browser and never leave it unencrypted. The passphrase you choose protects a backup of them that the
        server stores but cannot open. You need it to use your keys on another browser, and nobody can recover it for you.
      </p>
      <Problem message={problem} />
      <PassphraseForm
        idPrefix="setup"
        label="Recovery passphrase"
        submitLabel="Set up encryption"
        confirm
        busy={busy}
        onSubmit={(passphrase) => void run(() => setUpEncryption(passphrase))}
      />
    </div>
  )
}
