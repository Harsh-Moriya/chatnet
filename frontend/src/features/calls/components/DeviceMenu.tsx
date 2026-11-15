import { useEffect, useState } from 'react'
import { Camera, Check, Mic, Settings } from 'lucide-react'
import { cn } from '../../../lib/cn'
import type { CallActions } from '../../chat/hooks/chatConnectionContext'

interface Device {
  kind: 'audio' | 'video'
  id: string
  label: string
}

// The microphones and cameras to choose from, listed again when one is plugged in or out. A browser gives their names only once the
// microphone was allowed, which it has been by the time a call is running.
function useDevices(): Device[] {
  const [devices, setDevices] = useState<Device[]>([])

  useEffect(() => {
    const media = navigator.mediaDevices
    if (!media?.enumerateDevices) return

    let current = true
    const load = () =>
      media.enumerateDevices().then(
        (all) => {
          if (!current) return
          setDevices(
            all
              .filter((device) => device.kind === 'audioinput' || device.kind === 'videoinput')
              .map((device, index) => ({
                kind: device.kind === 'audioinput' ? 'audio' : 'video',
                id: device.deviceId,
                label: device.label || `${device.kind === 'audioinput' ? 'Microphone' : 'Camera'} ${index + 1}`,
              })),
          )
        },
        () => undefined,
      )

    void load()
    media.addEventListener('devicechange', load)
    return () => {
      current = false
      media.removeEventListener('devicechange', load)
    }
  }, [])

  return devices
}

interface DeviceMenuProps {
  // Whether this side sends video, so cameras are offered.
  video: boolean
  onSwitch: CallActions['switchDevice']
}

// Changes the microphone, or the camera, being sent, without ending the call. Left out when there is nothing to choose between. The list
// opens upward because the buttons are at the bottom of the screen; Escape closes it and does nothing else.
export function DeviceMenu({ video, onSwitch }: DeviceMenuProps) {
  const devices = useDevices()
  const [open, setOpen] = useState(false)
  const [chosen, setChosen] = useState<Record<Device['kind'], string | null>>({ audio: null, video: null })

  const choices = (['audio', 'video'] as const)
    .filter((kind) => kind === 'audio' || video)
    .map((kind) => ({ kind, devices: devices.filter((device) => device.kind === kind) }))
    .filter((group) => group.devices.length > 1)

  if (choices.length === 0) return null

  return (
    <div
      className="relative"
      onKeyDown={(event) => {
        if (event.key !== 'Escape' || !open) return
        event.preventDefault()
        event.stopPropagation()
        setOpen(false)
      }}
    >
      <button
        type="button"
        aria-label="Choose microphone or camera"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((value) => !value)}
        className="flex h-12 w-12 items-center justify-center rounded-full bg-slate-700 text-white transition-colors hover:bg-slate-600 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
      >
        <Settings className="h-5 w-5" aria-hidden="true" />
      </button>

      {open && (
        <div
          role="menu"
          aria-label="Choose microphone or camera"
          className="absolute bottom-full left-1/2 z-20 mb-2 w-64 max-w-[calc(100vw-2rem)] -translate-x-1/2 rounded-lg border border-slate-600 bg-slate-800 py-1 text-sm shadow-lg"
        >
          {choices.map((group) => (
            <div key={group.kind} role="group" aria-label={group.kind === 'audio' ? 'Microphone' : 'Camera'}>
              {group.devices.map((device) => (
                <button
                  key={device.id}
                  type="button"
                  role="menuitemradio"
                  aria-checked={chosen[device.kind] === device.id}
                  onClick={() => {
                    setChosen((current) => ({ ...current, [device.kind]: device.id }))
                    setOpen(false)
                    void onSwitch(device.kind, device.id)
                  }}
                  className={cn(
                    'flex w-full items-center gap-2 px-3 py-2 text-left text-slate-100 transition-colors',
                    'hover:bg-slate-700 focus-visible:bg-slate-700 focus-visible:outline-none',
                  )}
                >
                  {device.kind === 'audio' ? <Mic className="h-4 w-4 shrink-0" aria-hidden="true" /> : <Camera className="h-4 w-4 shrink-0" aria-hidden="true" />}
                  <span className="min-w-0 flex-1 truncate">{device.label}</span>
                  {chosen[device.kind] === device.id && <Check className="h-4 w-4 shrink-0" aria-hidden="true" />}
                </button>
              ))}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
