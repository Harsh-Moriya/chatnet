import { useEffect, useState } from 'react'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import { useMutation } from '@tanstack/react-query'
import { AlertCircle, Eye, EyeOff } from 'lucide-react'
import { AuthLayout } from './AuthLayout'
import { AuthNotice } from './AuthNotice'
import { resetPassword } from '../api/passwordResetApi'
import { endSessionAfterReset } from '../endSessionAfterReset'
import { PASSWORD_CHANGED_STATE } from '../notice'
import { readResetToken } from '../resetToken'
import { session } from '../session'
import { useAuthStore } from '../store/authStore'
import { Button } from '../../../components/ui/Button'
import { FormField } from '../../../components/ui/FormField'
import { getApiError, getFieldErrors } from '../../../lib/errors'

// ##### Reset password #####

export function ResetPasswordPage() {
  const location = useLocation()
  const navigate = useNavigate()

  // The token is read from the address into memory, and the address is then cleaned. The fragment never reaches a server, but it
  // would stay in the history and in the address bar. Nothing is put into the router state, which survives a reload. A link opened
  // in a tab that already shows this page changes only the fragment, so the address is read again each time it has one.
  const [token, setToken] = useState(() => readResetToken(location.hash))
  if (location.hash !== '') {
    const fromAddress = readResetToken(location.hash)
    if (fromAddress !== token) setToken(fromAddress)
  }

  useEffect(() => {
    if (location.hash !== '') navigate(location.pathname, { replace: true })
  }, [location.hash, location.pathname, navigate])

  // A different link starts from a clean form, with nothing left over from an earlier try.
  return <ResetForm key={token ?? 'none'} token={token} />
}

function ResetForm({ token }: { token: string | null }) {
  const navigate = useNavigate()

  const [newPassword, setNewPassword] = useState('')
  const [confirmation, setConfirmation] = useState('')
  const [showPassword, setShowPassword] = useState(false)
  const [mismatch, setMismatch] = useState(false)

  // 'finishing' is the moment after the server accepted the new password, while this browser's own session is looked at.
  const [phase, setPhase] = useState<'form' | 'finishing' | 'done'>('form')

  const reset = useMutation({
    mutationFn: (password: string) => resetPassword(token ?? '', password),
    onSuccess: async () => {
      setPhase('finishing')

      try {
        await endSessionAfterReset({
          status: () => useAuthStore.getState().status,
          refresh: () => session.refresh(),
          signOut: () => session.signOut(),
        })
      } catch {
        // The password is changed whatever went wrong with this browser's own session, so the page goes on to the sign-in page, which
        // is where the person needs to be, instead of staying on a spinner.
      }

      // Someone else may still be signed in here (the reset was for another account), and the sign-in page would send them on to
      // the chat. Then this page says it is done.
      if (useAuthStore.getState().status === 'authenticated') setPhase('done')
      else navigate('/', { replace: true, state: PASSWORD_CHANGED_STATE })
    },
  })

  const fields = reset.error ? getFieldErrors(reset.error) : {}
  const linkRejected = fields.token !== undefined

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()

    if (newPassword !== confirmation) {
      setMismatch(true)
      return
    }

    setMismatch(false)
    reset.mutate(newPassword)
  }

  if (token === null || linkRejected) {
    return (
      <AuthLayout title="This link cannot be used" subtitle="It may have expired, or it was already used">
        <p className="text-sm text-slate-600">
          Open the link from your email again, or ask for a new one.
        </p>
        <p className="mt-6 text-center text-sm text-slate-500">
          <Link to="/forgot-password" className="font-medium text-blue-600 hover:underline">
            Send me a new link
          </Link>
        </p>
      </AuthLayout>
    )
  }

  if (phase === 'done') {
    return (
      <AuthLayout title="Password changed" subtitle="Everyone who was signed in to that account was signed out">
        <AuthNotice>Your password was changed.</AuthNotice>
        <p className="mt-6 text-center text-sm text-slate-500">
          <Link to="/" className="font-medium text-blue-600 hover:underline">
            Continue
          </Link>
        </p>
      </AuthLayout>
    )
  }

  const generalError = reset.error && fields.newPassword === undefined ? getApiError(reset.error) : undefined

  return (
    <AuthLayout title="Choose a new password" subtitle="You will be signed out everywhere and asked to sign in again">
      <form onSubmit={handleSubmit} className="flex flex-col gap-4">
        {generalError && (
          <div className="flex items-start gap-2.5 rounded-lg border border-red-200 bg-red-50 px-3.5 py-3">
            <AlertCircle className="mt-0.5 h-4 w-4 shrink-0 text-red-500" />
            <p className="text-sm text-red-700">{generalError}</p>
          </div>
        )}

        <FormField
          label="New password"
          type={showPassword ? 'text' : 'password'}
          value={newPassword}
          onChange={e => setNewPassword(e.target.value)}
          placeholder="••••••••"
          autoComplete="new-password"
          error={fields.newPassword}
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

        <FormField
          label="Confirm new password"
          type={showPassword ? 'text' : 'password'}
          value={confirmation}
          onChange={e => {
            setConfirmation(e.target.value)
            setMismatch(false)
          }}
          placeholder="••••••••"
          autoComplete="new-password"
          error={mismatch ? 'The two passwords are not the same.' : undefined}
          required
        />

        <Button type="submit" fullWidth isLoading={reset.isPending || phase === 'finishing'} className="mt-1">
          Change password
        </Button>
      </form>
    </AuthLayout>
  )
}
