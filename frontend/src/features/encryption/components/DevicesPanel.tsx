import { useEffect, useState } from 'react'
import { CircleCheck, Laptop, Smartphone } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { getApiError } from '../../../lib/errors'
import { listDevices } from '../controller'
import { useEncryptionStore } from '../store'
import type { DeviceInfo } from '../types'
import { Problem } from './Problem'
import { RemoveDevicePanel } from './RemoveDevicePanel'

// With the time, so two devices of one kind added on one day can be told apart.
const when = (iso: string) => new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })

// The devices that hold the keys of the person, this browser marked, and a way to remove another one, as a device that is merely not
// wanted any more or as one that was lost.
export function DevicesPanel() {
  const thisDevice = useEncryptionStore((state) => state.deviceId)
  const [devices, setDevices] = useState<DeviceInfo[] | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [removing, setRemoving] = useState<string | null>(null)
  const [done, setDone] = useState<string | null>(null)
  // Counts the times the list was asked for again after something changed, so the effect below runs again.
  const [asked, setAsked] = useState(0)

  useEffect(() => {
    let current = true
    listDevices().then(
      (list) => {
        if (current) {
          setDevices(list)
          setProblem(null)
        }
      },
      (failure: unknown) => {
        if (current) setProblem(getApiError(failure))
      },
    )
    return () => {
      current = false
    }
  }, [asked])

  return (
    <div className="space-y-2">
      <h4 className="text-sm font-medium text-slate-700">Devices with your keys</h4>
      <Problem message={problem} />
      {done !== null && (
        <p role="status" className="flex items-start gap-2 text-xs text-emerald-700">
          <CircleCheck className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
          <span>{done}</span>
        </p>
      )}
      {devices === null && problem === null && <p className="text-xs text-slate-500">Loading...</p>}
      {devices && (
        <ul className="divide-y divide-line-subtle rounded-lg border border-line">
          {devices.map((device) => (
            <li key={device.id} className="px-3 py-2">
              <div className="flex items-center gap-3">
                {device.kind === 'Web' ? (
                  <Laptop className="h-4 w-4 shrink-0 text-slate-500" aria-hidden="true" />
                ) : (
                  <Smartphone className="h-4 w-4 shrink-0 text-slate-500" aria-hidden="true" />
                )}
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm text-slate-900">
                    {device.name}
                    {device.id === thisDevice && <span className="ml-2 rounded bg-blue-50 px-1.5 py-0.5 text-xs text-blue-700">This browser</span>}
                  </p>
                  <p className="text-xs text-slate-500">Added {when(device.createdAt)}, last used {when(device.lastActiveAt)}</p>
                </div>
                {device.id !== thisDevice && removing !== device.id && (
                  <Button
                    variant="ghost"
                    size="sm"
                    aria-label={`Remove ${device.name}`}
                    onClick={() => {
                      setDone(null)
                      setRemoving(device.id)
                    }}
                  >
                    Remove
                  </Button>
                )}
              </div>
              {removing === device.id && (
                <RemoveDevicePanel
                  device={device}
                  onCancel={() => setRemoving(null)}
                  onRemoved={(message) => {
                    setRemoving(null)
                    setDone(message)
                    setAsked((count) => count + 1)
                  }}
                />
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
