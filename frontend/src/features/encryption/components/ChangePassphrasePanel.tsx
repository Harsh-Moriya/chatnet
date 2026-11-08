import { useState } from 'react'
import { CircleCheck } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { changePassphrase } from '../controller'
import { PassphraseForm } from './PassphraseForm'
import { Problem } from './Problem'
import { useStep } from './useStep'

// A new recovery passphrase for the same keys. The old one stops opening the backup.
export function ChangePassphrasePanel() {
  const { busy, problem, run, clear } = useStep()
  const [open, setOpen] = useState(false)
  const [changed, setChanged] = useState(false)

  if (!open) {
    return (
      <div className="space-y-2">
        {changed && (
          <p role="status" className="flex items-center gap-2 text-xs text-emerald-700">
            <CircleCheck className="h-3.5 w-3.5" aria-hidden="true" />
            Your recovery passphrase was changed.
          </p>
        )}
        <Button
          variant="ghost"
          size="sm"
          onClick={() => {
            setChanged(false)
            setOpen(true)
          }}
        >
          Change recovery passphrase
        </Button>
      </div>
    )
  }

  return (
    <div className="space-y-3">
      <h4 className="text-sm font-medium text-slate-700">Change recovery passphrase</h4>
      <Problem message={problem} />
      <PassphraseForm
        idPrefix="change"
        label="New recovery passphrase"
        submitLabel="Change passphrase"
        confirm
        busy={busy}
        onSubmit={(passphrase) =>
          void run(() => changePassphrase(passphrase)).then((worked) => {
            if (worked) {
              setChanged(true)
              setOpen(false)
            }
          })
        }
      />
      <Button
        variant="ghost"
        size="sm"
        onClick={() => {
          clear()
          setOpen(false)
        }}
      >
        Cancel
      </Button>
    </div>
  )
}
