import { type FormEvent, useState } from 'react'
import { TriangleAlert } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { FormField } from '../../../components/ui/FormField'
import { resetKeys } from '../controller'
import { passphraseProblem } from '../passphrase'
import { Problem } from './Problem'
import { useStep } from './useStep'

// For a person who has neither another device nor the recovery passphrase: start over with new keys. What this costs is said first and
// plainly, and the account password is asked again, because this is not something to do by accident.
export function ResetKeysPanel({ onCancel }: { onCancel: () => void }) {
  const { busy, problem, run } = useStep()
  const [passphrase, setPassphrase] = useState('')
  const [repeat, setRepeat] = useState('')
  const [password, setPassword] = useState('')
  const [attempted, setAttempted] = useState(false)

  const passphraseIssue = passphraseProblem(passphrase, repeat)
  const passwordIssue = password === '' ? 'Enter your account password.' : null

  function submit(event: FormEvent) {
    event.preventDefault()
    setAttempted(true)
    if (passphraseIssue === null && passwordIssue === null) void run(() => resetKeys(passphrase, password))
  }

  return (
    <form onSubmit={submit} noValidate className="space-y-3">
      <div role="note" className="flex items-start gap-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
        <TriangleAlert className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
        <div className="space-y-1">
          <p className="font-medium">Starting over cannot be undone.</p>
          <ul className="list-disc space-y-0.5 pl-4">
            <li>Your old messages cannot be opened on any device, by you or by anyone.</li>
            <li>Every device you had is removed and signed out.</li>
            <li>The people you talk to are told your security key changed, and have to accept the new one before they can write to you.</li>
          </ul>
        </div>
      </div>
      <Problem message={problem} />
      <FormField
        id="reset-passphrase"
        label="New recovery passphrase"
        type="password"
        autoComplete="off"
        value={passphrase}
        onChange={(event) => setPassphrase(event.target.value)}
      />
      <FormField
        id="reset-repeat"
        label="Repeat the passphrase"
        type="password"
        autoComplete="off"
        value={repeat}
        onChange={(event) => setRepeat(event.target.value)}
      />
      <FormField
        id="reset-password"
        label="Account password"
        type="password"
        autoComplete="current-password"
        value={password}
        onChange={(event) => setPassword(event.target.value)}
      />
      {attempted && (passphraseIssue ?? passwordIssue) !== null && (
        <p role="alert" className="text-xs text-red-500">
          {passphraseIssue ?? passwordIssue}
        </p>
      )}
      <div className="flex flex-wrap gap-2">
        <Button type="submit" isLoading={busy}>
          Start over with new keys
        </Button>
        <Button type="button" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </form>
  )
}
