import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Eye, EyeOff } from 'lucide-react'
import type { LoginRequest } from '../types'
import { FormField } from '../../../components/ui/FormField'
import { Button } from '../../../components/ui/Button'
import { Alert } from '../../../components/ui/Alert'

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
