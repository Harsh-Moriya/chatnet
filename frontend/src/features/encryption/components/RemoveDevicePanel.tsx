import { useState } from 'react'
import { Button } from '../../../components/ui/Button'
import { removeDevice, removeLostDevice } from '../controller'
import type { DeviceInfo } from '../types'
import { PassphraseForm } from './PassphraseForm'
import { Problem } from './Problem'
import { useStep } from './useStep'

interface RemoveDevicePanelProps {
  device: DeviceInfo
  onCancel: () => void
  // Says what was done, for the list to show.
  onRemoved: (message: string) => void
}

// Removing a device signs it out. If it was lost or stolen that is not enough, because it still holds the keys: renewing them makes
// what is sent from now on unreadable to it. That asks for the recovery passphrase, which proves the person is asking.
export function RemoveDevicePanel({ device, onCancel, onRemoved }: RemoveDevicePanelProps) {
  const { busy, problem, run, clear } = useStep()
  const [lost, setLost] = useState(false)

  return (
    <div className="mt-2 space-y-3 rounded-lg border border-line bg-slate-50 p-3">
      <p className="text-xs text-slate-600">
        Removing {device.name} signs it out of your account. If it is only a device you no longer use, that is enough.
      </p>
      <Problem message={problem} />

      {!lost && (
        <div className="flex flex-wrap gap-2">
          <Button
            size="sm"
            isLoading={busy}
            onClick={() => void run(() => removeDevice(device.id)).then((worked) => worked && onRemoved(`${device.name} was removed.`))}
          >
            Remove it
          </Button>
          <Button
            variant="ghost"
            size="sm"
            onClick={() => {
              clear()
              setLost(true)
            }}
          >
            It was lost or stolen
          </Button>
          <Button variant="ghost" size="sm" onClick={onCancel}>
            Cancel
          </Button>
        </div>
      )}

      {lost && (
        <div className="space-y-3">
          <p className="text-xs text-slate-600">
            A lost device still holds your keys. Renewing them means it cannot open anything sent from now on. It can still read what it
            already had. Your other devices will ask for your recovery passphrase once, to get the new keys, and the people you talk to see
            nothing change.
          </p>
          <PassphraseForm
            idPrefix={`lost-${device.id}`}
            label="Recovery passphrase"
            submitLabel="Remove it and renew my keys"
            busy={busy}
            onSubmit={(passphrase) =>
              void run(() => removeLostDevice(passphrase, device.id)).then(
                (worked) => worked && onRemoved(`${device.name} was removed and your keys were renewed.`),
              )
            }
          />
          <Button variant="ghost" size="sm" onClick={onCancel}>
            Cancel
          </Button>
        </div>
      )}
    </div>
  )
}
