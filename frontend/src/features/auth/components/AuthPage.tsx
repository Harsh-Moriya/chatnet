import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { LoginForm } from './LoginForm'
import { RegisterForm } from './RegisterForm'
import { useLogin } from '../api/useLogin'
import { useRegister } from '../api/useRegister'
import { getApiError } from '../../../lib/errors'
import { useAuthStore } from '../store/authStore'
import type { AuthResponse } from '../types'

export function AuthPage() {
  const [mode, setMode] = useState<'login' | 'register'>('login')
  const navigate = useNavigate()
  const { setAuth } = useAuthStore()
  const loginMutation = useLogin()
  const registerMutation = useRegister()

  function handleSuccess(response: AuthResponse) {
    setAuth(response)
    navigate('/')
  }

  return (
    <div className="min-h-screen flex items-center justify-center bg-gray-50">
      <div className="bg-white rounded-xl shadow-sm border border-gray-200 w-full max-w-sm p-8">

        <div className="mb-8">
          <h1 className="text-2xl font-semibold text-gray-900">
            {mode === 'login' ? 'Welcome back' : 'Create an account'}
          </h1>
          <p className="text-sm text-gray-500 mt-1">ChatNET</p>
        </div>

        {mode === 'login' ? (
          <LoginForm
            onSubmit={data =>
              loginMutation.mutate(data, {
                onSuccess: r => handleSuccess(r),
              })
            }
            isLoading={loginMutation.isPending}
            error={loginMutation.error ? getApiError(loginMutation.error) : undefined}
          />
        ) : (
          <RegisterForm
            onSubmit={data =>
              registerMutation.mutate(data, {
                onSuccess: r => handleSuccess(r),
              })
            }
            isLoading={registerMutation.isPending}
            error={registerMutation.error ? getApiError(registerMutation.error) : undefined}
          />
        )}

        <p className="text-sm text-gray-500 text-center mt-6">
          {mode === 'login' ? "Don't have an account?" : 'Already have an account?'}{' '}
          <button
            type="button"
            onClick={() => {
              setMode(mode === 'login' ? 'register' : 'login')
              loginMutation.reset()
              registerMutation.reset()
            }}
            className="text-indigo-600 font-medium hover:underline"
          >
            {mode === 'login' ? 'Register' : 'Sign in'}
          </button>
        </p>

      </div>
    </div>
  )
}
