import { type FormEvent, useState } from 'react'
import { AlertCircle } from 'lucide-react'
import { AvatarPicker } from '../../../components/ui/AvatarPicker'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { Input } from '../../../components/ui/Input'
import { getApiError } from '../../../lib/errors'
import { useAuth } from '../../auth/hooks/useAuth'
import { removePicture, setPicture, updateDisplayName } from '../api/profileApi'
import { prepareAvatar } from '../avatarImage'

// The longest name the server keeps (the limit registration has).
const MAX_DISPLAY_NAME_LENGTH = 100

interface ProfileDialogProps {
  open: boolean
  onClose: () => void
}

export function ProfileDialog({ open, onClose }: ProfileDialogProps) {
  return (
    <Dialog open={open} onClose={onClose} title="Your profile">
      <Profile />
    </Dialog>
  )
}

// Rendered only while the dialog is open (see Dialog), so every opening starts as the profile is.
function Profile() {
  const { user } = useAuth()
  const [name, setName] = useState(user?.displayName ?? '')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // What every change needs: the dialog is busy while it runs, a refusal is shown, and nothing on the screen changes until
  // the server has accepted it (the request function puts the answer into the stores).
  async function run(action: () => Promise<unknown>) {
    setBusy(true)
    try {
      await action()
      setError(null)
    } catch (failure) {
      setError(getApiError(failure))
    } finally {
      setBusy(false)
    }
  }

  const trimmed = name.trim()
  const canSave = trimmed !== '' && trimmed !== user?.displayName

  async function save(event: FormEvent) {
    event.preventDefault()
    if (canSave) await run(() => updateDisplayName(trimmed))
  }

  return (
    <div className="flex min-h-0 flex-col gap-5 overflow-y-auto p-5">
      {error && (
        <div role="alert" className="flex items-start gap-2 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-600">
          <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" />
          <span>{error}</span>
        </div>
      )}

      <AvatarPicker
        name={user?.displayName ?? ''}
        src={user?.avatarUrl ?? null}
        busy={busy}
        onPick={(file) => void run(async () => setPicture({ kind: 'me' }, await prepareAvatar(file)))}
        onRemove={() => void run(() => removePicture({ kind: 'me' }))}
      />

      <form onSubmit={(event) => void save(event)} className="space-y-1.5">
        <label htmlFor="display-name" className="block text-sm font-medium text-slate-700">
          Display name
        </label>
        <div className="flex items-start gap-2">
          <Input
            id="display-name"
            maxLength={MAX_DISPLAY_NAME_LENGTH}
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
          <Button type="submit" disabled={!canSave || busy}>
            Save
          </Button>
        </div>
        <p className="text-xs text-slate-500">@{user?.userName} is your handle and does not change.</p>
      </form>
    </div>
  )
}
