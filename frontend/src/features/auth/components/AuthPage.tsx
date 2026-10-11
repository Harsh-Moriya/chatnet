import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { MessagesSquare } from 'lucide-react'
import { LoginForm } from './LoginForm'
import { RegisterForm } from './RegisterForm'
import { useLogin } from '../api/useLogin'
import { useRegister } from '../api/useRegister'
import { getApiError } from '../../../lib/errors'
import { useAuthStore } from '../store/authStore'
import type { AuthResponse } from '../types'

// ##### Brand panel #####

function BrandPanel() {
  return (
    <div className="hidden lg:flex lg:w-[45%] flex-col justify-between bg-gradient-to-br from-blue-700 to-sky-500 p-10 text-white">
      <div className="flex items-center gap-2.5">
        <MessagesSquare className="h-6 w-6" />
        <span className="text-base font-semibold tracking-tight">ChatNET</span>
      </div>

      <div>
        <h1 className="text-3xl font-bold leading-snug">
          Real-time conversations<br />for your team.
        </h1>
        <p className="mt-3 max-w-xs text-sm leading-relaxed text-blue-100">
          Simple, fast, and reliable messaging. Built for how teams actually work.
        </p>
      </div>

      <p className="text-xs text-blue-200/60">© 2026 ChatNET</p>
    </div>
  )
}

// ##### Auth page #####

export function AuthPage() {
  const [mode, setMode] = useState<'login' | 'register'>('login')
  const navigate = useNavigate()
  const { setAuth } = useAuthStore()
  const loginMutation = useLogin()
  const registerMutation = useRegister()

  function handleSuccess(response: AuthResponse) {
    setAuth(response)
    navigate('/chat')
  }

  function toggleMode() {
    setMode(mode === 'login' ? 'register' : 'login')
    loginMutation.reset()
    registerMutation.reset()
  }

  return (
    <div className="min-h-screen flex">
      <BrandPanel />

      <div className="flex-1 flex flex-col items-center justify-center p-8 bg-slate-50">
        <div className="w-full max-w-sm">
          {/* Logo shown only on mobile where BrandPanel is hidden */}
          <div className="mb-8 flex items-center gap-2 lg:hidden">
            <MessagesSquare className="h-6 w-6 text-blue-600" />
            <span className="font-semibold text-slate-900">ChatNET</span>
          </div>

          <div className="mb-7">
            <h2 className="text-2xl font-semibold text-slate-900">
              {mode === 'login' ? 'Welcome back' : 'Create your account'}
            </h2>
            <p className="mt-1 text-sm text-slate-500">
              {mode === 'login'
                ? 'Sign in to continue to ChatNET'
                : 'Get started with ChatNET today'}
            </p>
          </div>

          {mode === 'login' ? (
            <LoginForm
              onSubmit={data =>
                loginMutation.mutate(data, { onSuccess: r => handleSuccess(r) })
              }
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
        </div>
      </div>
    </div>
  )
}
