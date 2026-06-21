import { useMutation } from '@tanstack/react-query'
import { api } from '../../../lib/api'
import type { RegisterRequest, AuthResponse } from '../types'

export function useRegister() {
  return useMutation({
    mutationFn: (data: RegisterRequest) =>
      api.post<AuthResponse>('/auth/register', data).then(r => r.data),
  })
}
