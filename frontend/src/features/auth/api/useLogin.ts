import { useMutation } from '@tanstack/react-query'
import { api } from '../../../lib/api'
import type { LoginRequest, AuthResponse } from '../types'

export function useLogin() {
  return useMutation({
    mutationFn: (data: LoginRequest) =>
      api.post<AuthResponse>('/auth/login', data).then(r => r.data),
  })
}
