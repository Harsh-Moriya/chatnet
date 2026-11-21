import { useState } from 'react'
import { Eye, EyeOff } from 'lucide-react'
import type { RegisterRequest } from '../types'
import { FormField } from '../../../components/ui/FormField'
import { Button } from '../../../components/ui/Button'
import { Alert } from '../../../components/ui/Alert'

interface Props {
  onSubmit: (data: RegisterRequest) => void
  isLoading: boolean
  error?: string
}

export function RegisterForm({ onSubmit, isLoading, error }: Props) {
  const [userName, setUserName] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [showPassword, setShowPassword] = useState(false)

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    onSubmit({ userName, displayName, email, password })
  }

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-4">
      {error && (
        <Alert>{error}</Alert>
      )}

      <div className="grid grid-cols-2 gap-3">
        <FormField
          label="Username"
          type="text"
          value={userName}
          onChange={e => setUserName(e.target.value)}
          placeholder="e.g. alex_dev"
          autoComplete="username"
          required
        />
        <FormField
          label="Display name"
          type="text"
          value={displayName}
          onChange={e => setDisplayName(e.target.value)}
          placeholder="Alex Chen"
          autoComplete="name"
          required
        />
      </div>

      <FormField
        label="Email"
        type="email"
        value={email}
        onChange={e => setEmail(e.target.value)}
        placeholder="you@example.com"
        autoComplete="email"
        required
      />

      <FormField
        label="Password"
        type={showPassword ? 'text' : 'password'}
        value={password}
        onChange={e => setPassword(e.target.value)}
        placeholder="minimum 8 characters"
        autoComplete="new-password"
        required
        rightElement={
          <button
            type="button"
            onClick={() => setShowPassword(v => !v)}
            className="text-fg-faint hover:text-fg-soft transition-colors"
            aria-label={showPassword ? 'Hide password' : 'Show password'}
          >
            {showPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
          </button>
        }
      />

      <Button type="submit" fullWidth isLoading={isLoading} className="mt-1">
        Create account
      </Button>
    </form>
  )
}
