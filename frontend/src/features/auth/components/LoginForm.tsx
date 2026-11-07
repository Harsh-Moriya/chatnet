import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Eye, EyeOff, AlertCircle } from 'lucide-react'
import type { LoginRequest } from '../types'
import { FormField } from '../../../components/ui/FormField'
import { Button } from '../../../components/ui/Button'

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
        <div className="flex items-start gap-2.5 rounded-lg border border-red-200 bg-red-50 px-3.5 py-3">
          <AlertCircle className="h-4 w-4 shrink-0 text-red-500 mt-0.5" />
          <p className="text-sm text-red-700">{error}</p>
        </div>
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
            className="text-slate-400 hover:text-slate-600 transition-colors"
            aria-label={showPassword ? 'Hide password' : 'Show password'}
          >
            {showPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
          </button>
        }
      />

      <div className="-mt-2 text-right">
        <Link to="/forgot-password" className="text-sm font-medium text-blue-600 hover:underline">
          Forgot password?
        </Link>
      </div>

      <Button type="submit" fullWidth isLoading={isLoading} className="mt-1">
        Sign in
      </Button>
    </form>
  )
}
