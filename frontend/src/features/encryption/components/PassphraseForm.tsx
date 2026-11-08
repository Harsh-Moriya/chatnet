import { type FormEvent, useState } from 'react'
import { Button } from '../../../components/ui/Button'
import { FormField } from '../../../components/ui/FormField'
import { passphraseProblem } from '../passphrase'

interface PassphraseFormProps {
  // Names the field and keeps the ids of two forms on one page apart.
  idPrefix: string
  label: string
  submitLabel: string
  // A new passphrase: typed twice and checked for length. Without it the field only has to be filled in, because it is a passphrase
  // that already exists and the backup decides whether it is right.
  confirm?: boolean
  busy: boolean
  onSubmit: (passphrase: string) => void
}

export function PassphraseForm({ idPrefix, label, submitLabel, confirm = false, busy, onSubmit }: PassphraseFormProps) {
  const [passphrase, setPassphrase] = useState('')
  const [repeat, setRepeat] = useState('')
  const [attempted, setAttempted] = useState(false)

  const problem = confirm ? passphraseProblem(passphrase, repeat) : passphrase === '' ? 'Enter your passphrase.' : null

  function submit(event: FormEvent) {
    event.preventDefault()
    setAttempted(true)
    if (problem === null) onSubmit(passphrase)
  }

  return (
    <form onSubmit={submit} noValidate className="space-y-3">
      <FormField
        id={`${idPrefix}-passphrase`}
        label={label}
        type="password"
        // These are not account passwords, and a password manager offering to save one as such would be wrong.
        autoComplete="off"
        value={passphrase}
        onChange={(event) => setPassphrase(event.target.value)}
      />
      {confirm && (
        <FormField
          id={`${idPrefix}-repeat`}
          label="Repeat the passphrase"
          type="password"
          autoComplete="off"
          value={repeat}
          onChange={(event) => setRepeat(event.target.value)}
        />
      )}
      {attempted && problem !== null && (
        <p role="alert" className="text-xs text-red-500">
          {problem}
        </p>
      )}
      <Button type="submit" isLoading={busy}>
        {submitLabel}
      </Button>
    </form>
  )
}
