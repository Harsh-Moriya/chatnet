import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useMutation } from '@tanstack/react-query'
import { AlertCircle } from 'lucide-react'
import { AuthLayout } from './AuthLayout'
import { AuthNotice } from './AuthNotice'
import { requestPasswordReset } from '../api/passwordResetApi'
import { Button } from '../../../components/ui/Button'
import { FormField } from '../../../components/ui/FormField'
import { getApiError, getFieldErrors } from '../../../lib/errors'

// ##### Forgot password #####

export function ForgotPasswordPage() {
  const [email, setEmail] = useState('')
  const [sent, setSent] = useState(false)
  const request = useMutation({ mutationFn: requestPasswordReset, onSuccess: () => setSent(true) })

  const fieldError = request.error ? getFieldErrors(request.error).email : undefined

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    request.mutate(email.trim())
  }

  return (
    <AuthLayout
      title="Forgot your password?"
      subtitle="Enter your email and we will send you a link to choose a new one"
    >
      {sent ? (
        // The same words whether or not the address has an account: the server does not say, and neither does this page.
        <AuthNotice>
          If there is an account for that address, an email with a link is on its way. The link stops working after a short time.
        </AuthNotice>
      ) : (
        <form onSubmit={handleSubmit} className="flex flex-col gap-4">
          {request.error && !fieldError && (
            <div className="flex items-start gap-2.5 rounded-lg border border-red-200 bg-red-50 px-3.5 py-3">
              <AlertCircle className="mt-0.5 h-4 w-4 shrink-0 text-red-500" />
              <p className="text-sm text-red-700">{getApiError(request.error)}</p>
            </div>
          )}

          <FormField
            label="Email"
            type="email"
            value={email}
            onChange={e => setEmail(e.target.value)}
            placeholder="you@example.com"
            autoComplete="email"
            error={fieldError}
            required
          />

          <Button type="submit" fullWidth isLoading={request.isPending} className="mt-1">
            Send reset link
          </Button>
        </form>
      )}

      <p className="mt-6 text-center text-sm text-slate-500">
        <Link to="/" className="font-medium text-blue-600 hover:underline">
          Back to sign in
        </Link>
      </p>
    </AuthLayout>
  )
}
