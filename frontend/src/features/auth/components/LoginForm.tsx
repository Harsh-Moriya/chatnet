import { useState } from 'react'
import { Link } from 'react-router-dom'
import type { LoginRequest } from '../types'
import { FormField } from '../../../components/ui/FormField'
import { Button } from '../../../components/ui/Button'
import { Alert } from '../../../components/ui/Alert'
import { PasswordToggle } from './PasswordToggle'

interface Props {
  onSubmit: (data: LoginRequest) => void
  isLoading: boolean
  error?: string
}

export function LoginForm({ onSubmit, isLoading, error }: Props) {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [showPassword, setShowPassword] = useState(false)

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    onSubmit({ email, password })
  }

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-4">
      {error && (
        <Alert>{error}</Alert>
      )}

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
        placeholder="••••••••"
        autoComplete="current-password"
        required
        rightElement={
          <PasswordToggle shown={showPassword} onToggle={() => setShowPassword(v => !v)} />
        }
      />

      <div className="-mt-2 text-right">
        <Link to="/forgot-password" className="text-sm font-medium text-accent-fg hover:underline">
          Forgot password?
        </Link>
      </div>

      <Button type="submit" fullWidth isLoading={isLoading} className="mt-1">
        Sign in
      </Button>
    </form>
  )
}
