import { type FormEvent, useState } from 'react'
import { Alert } from '../../../components/ui/Alert'
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
      <Alert kind="warning" size="sm" role="note">
        <div className="space-y-1">
          <p className="font-medium">Starting over cannot be undone.</p>
          <ul className="list-disc space-y-0.5 pl-4">
            <li>Your old messages cannot be opened on any device, by you or by anyone.</li>
            <li>Every device you had is removed and signed out.</li>
            <li>The people you talk to are told your security key changed, and have to accept the new one before they can write to you.</li>
          </ul>
        </div>
      </Alert>
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
        <p role="alert" className="text-xs text-danger-fg">
          {passphraseIssue ?? passwordIssue}
        </p>
      )}
      <div className="flex flex-wrap gap-2">
        <Button type="submit" isLoading={busy}>
          Start over with new keys
        </Button>
        <Button type="button" variant="secondary" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </form>
  )
}
