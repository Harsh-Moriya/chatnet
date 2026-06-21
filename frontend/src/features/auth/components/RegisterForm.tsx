import { useState } from 'react'
import type { RegisterRequest } from '../types'

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

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    onSubmit({ userName, displayName, email, password })
  }

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-4">
      {error && (
        <p className="text-sm text-red-600 bg-red-50 border border-red-200 px-3 py-2 rounded-md">
          {error}
        </p>
      )}

      <div className="flex flex-col gap-1">
        <label htmlFor="userName" className="text-sm font-medium text-gray-700">
          Username
        </label>
        <input
          id="userName"
          type="text"
          value={userName}
          onChange={e => setUserName(e.target.value)}
          required
          autoComplete="username"
          placeholder="letters, numbers, underscores"
          className="border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
        />
      </div>

      <div className="flex flex-col gap-1">
        <label htmlFor="displayName" className="text-sm font-medium text-gray-700">
          Display name
        </label>
        <input
          id="displayName"
          type="text"
          value={displayName}
          onChange={e => setDisplayName(e.target.value)}
          required
          autoComplete="name"
          placeholder="shown to other users"
          className="border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
        />
      </div>

      <div className="flex flex-col gap-1">
        <label htmlFor="reg-email" className="text-sm font-medium text-gray-700">
          Email
        </label>
        <input
          id="reg-email"
          type="email"
          value={email}
          onChange={e => setEmail(e.target.value)}
          required
          autoComplete="email"
          className="border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
        />
      </div>

      <div className="flex flex-col gap-1">
        <label htmlFor="reg-password" className="text-sm font-medium text-gray-700">
          Password
        </label>
        <input
          id="reg-password"
          type="password"
          value={password}
          onChange={e => setPassword(e.target.value)}
          required
          autoComplete="new-password"
          placeholder="minimum 8 characters"
          className="border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
        />
      </div>

      <button
        type="submit"
        disabled={isLoading}
        className="bg-indigo-600 text-white rounded-md px-4 py-2 text-sm font-medium hover:bg-indigo-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
      >
        {isLoading ? 'Creating account…' : 'Create account'}
      </button>
    </form>
  )
}
