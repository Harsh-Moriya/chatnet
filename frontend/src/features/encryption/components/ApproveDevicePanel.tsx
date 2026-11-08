import { type FormEvent, useState } from 'react'
import { CircleCheck } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { FormField } from '../../../components/ui/FormField'
import { approveDevice, findDeviceToLink } from '../controller'
import type { LinkRequestInfo } from '../types'
import { Problem } from './Problem'
import { useStep } from './useStep'

// On a device that is signed in: type the code a new device shows, see which device asks, and hand it the keys.
export function ApproveDevicePanel() {
  const { busy, problem, run, clear } = useStep()
  const [code, setCode] = useState('')
  const [found, setFound] = useState<LinkRequestInfo | null>(null)
  const [approved, setApproved] = useState<string | null>(null)

  async function find(event: FormEvent) {
    event.preventDefault()
    setApproved(null)
    await run(async () => setFound(await findDeviceToLink(code)))
  }

  async function approve() {
    if (found === null) return
    if (await run(() => approveDevice(found))) {
      setApproved(found.name)
      setFound(null)
      setCode('')
    }
  }

  return (
    <div className="space-y-3">
      <p className="text-xs text-slate-500">
        To use your keys on another browser or phone, sign in there, choose Link from another device, and enter the code it shows.
      </p>
      <Problem message={problem} />

      {approved !== null && (
        <p role="status" className="flex items-center gap-2 text-xs text-emerald-700">
          <CircleCheck className="h-3.5 w-3.5" aria-hidden="true" />
          {approved} now has your keys.
        </p>
      )}

      {found === null ? (
        <form onSubmit={(event) => void find(event)} noValidate className="flex items-end gap-2">
          <div className="flex-1">
            <FormField
              id="link-code"
              label="Link code"
              autoComplete="off"
              autoCapitalize="characters"
              spellCheck={false}
              placeholder="XXXXX-XXXXX"
              value={code}
              onChange={(event) => setCode(event.target.value)}
            />
          </div>
          <Button type="submit" isLoading={busy} disabled={code.trim() === ''}>
            Find
          </Button>
        </form>
      ) : (
        <div className="space-y-3 rounded-lg border border-line p-3">
          <p className="text-sm text-slate-900">
            <span className="font-medium">{found.name}</span> is asking for your keys.
          </p>
          <p className="text-xs text-slate-500">
            Approve only if you started this on a device you are holding and the code on its screen is the one you typed. Whoever you
            approve can read your messages.
          </p>
          <div className="flex gap-2">
            <Button isLoading={busy} onClick={() => void approve()}>
              Approve
            </Button>
            <Button
              variant="ghost"
              onClick={() => {
                setFound(null)
                clear()
              }}
            >
              Cancel
            </Button>
          </div>
        </div>
      )}
    </div>
  )
}
