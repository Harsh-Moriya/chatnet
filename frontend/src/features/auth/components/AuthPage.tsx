import { useEffect, useState } from 'react'
import { Navigate, useLocation, useNavigate } from 'react-router-dom'
import { AuthLayout } from './AuthLayout'
import { AuthNotice } from './AuthNotice'
import { LoginForm } from './LoginForm'
import { RegisterForm } from './RegisterForm'
import { useLogin } from '../api/useLogin'
import { useRegister } from '../api/useRegister'
import { getApiError } from '../../../lib/errors'
import { SessionLoading } from '../../../components/SessionLoading'
import { useAuth } from '../hooks/useAuth'
import { useAuthStore } from '../store/authStore'
import { wasPasswordChanged } from '../notice'
import type { AuthResponse } from '../types'

// ##### Auth page #####

export function AuthPage() {
  const [mode, setMode] = useState<'login' | 'register'>('login')
  const { isAuthenticated, isResolving } = useAuth()
  const setAuth = useAuthStore((state) => state.setAuth)
  const loginMutation = useLogin()
  const registerMutation = useRegister()

  // The reset page sends the person here after a new password was set. The notice is read once and the router state is then
  // removed, so a reload does not show it again.
  const location = useLocation()
  const navigate = useNavigate()
  const [passwordChanged, setPasswordChanged] = useState(() => wasPasswordChanged(location.state))
  useEffect(() => {
    if (wasPasswordChanged(location.state)) navigate(location.pathname, { replace: true })
  }, [location.state, location.pathname, navigate])

  // Signing in flips the store, and the redirect below takes the person to the chat.
  function handleSuccess(response: AuthResponse) {
    setAuth(response)
  }

  function toggleMode() {
    setMode(mode === 'login' ? 'register' : 'login')
    loginMutation.reset()
    registerMutation.reset()
  }

  // Someone who still has a working session (a reload, a second tab, the address typed in again) has no
  // use for the form. Until that is known, showing it would flash the wrong page.
  if (isResolving) return <SessionLoading />
  if (isAuthenticated) return <Navigate to="/chat" replace />

  return (
    <AuthLayout
      title={mode === 'login' ? 'Welcome back' : 'Create your account'}
      subtitle={mode === 'login' ? 'Sign in to continue to ChatNET' : 'Get started with ChatNET today'}
    >
      {passwordChanged && mode === 'login' && (
        <div className="mb-4">
          <AuthNotice>Your password was changed. Sign in with the new one.</AuthNotice>
        </div>
      )}

      {mode === 'login' ? (
        <LoginForm
          onSubmit={data => {
            setPasswordChanged(false)
            loginMutation.mutate(data, { onSuccess: r => handleSuccess(r) })
          }}
          isLoading={loginMutation.isPending}
          error={loginMutation.error ? getApiError(loginMutation.error) : undefined}
        />
      ) : (
        <RegisterForm
          onSubmit={data =>
            registerMutation.mutate(data, { onSuccess: r => handleSuccess(r) })
          }
          isLoading={registerMutation.isPending}
          error={registerMutation.error ? getApiError(registerMutation.error) : undefined}
        />
      )}

      <p className="mt-6 text-center text-sm text-slate-500">
        {mode === 'login' ? "Don't have an account?" : 'Already have an account?'}{' '}
        <button
          type="button"
          onClick={toggleMode}
          className="font-medium text-blue-600 hover:underline"
        >
          {mode === 'login' ? 'Register' : 'Sign in'}
        </button>
      </p>
    </AuthLayout>
  )
}
