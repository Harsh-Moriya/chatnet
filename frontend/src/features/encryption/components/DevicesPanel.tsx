import { useEffect, useState } from 'react'
import { Laptop, Smartphone } from 'lucide-react'
import { getApiError } from '../../../lib/errors'
import { listDevices } from '../controller'
import { useEncryptionStore } from '../store'
import type { DeviceInfo } from '../types'
import { Problem } from './Problem'

// With the time, so two devices of one kind added on one day can be told apart.
const when = (iso: string) => new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })

// The devices that hold the keys of the person, this browser marked.
export function DevicesPanel() {
  const thisDevice = useEncryptionStore((state) => state.deviceId)
  const [devices, setDevices] = useState<DeviceInfo[] | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  useEffect(() => {
    let current = true
    listDevices().then(
      (list) => {
        if (current) setDevices(list)
      },
      (failure: unknown) => {
        if (current) setProblem(getApiError(failure))
      },
    )
    return () => {
      current = false
    }
  }, [])

  return (
    <div className="space-y-2">
      <h4 className="text-sm font-medium text-slate-700">Devices with your keys</h4>
      <Problem message={problem} />
      {devices === null && problem === null && <p className="text-xs text-slate-500">Loading...</p>}
      {devices && (
        <ul className="divide-y divide-line-subtle rounded-lg border border-line">
          {devices.map((device) => (
            <li key={device.id} className="flex items-center gap-3 px-3 py-2">
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
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
