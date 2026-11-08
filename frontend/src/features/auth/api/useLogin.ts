import { useMutation } from '@tanstack/react-query'
import { api } from '../../../lib/api'
import { deviceHeaders } from '../session'
import type { LoginRequest, AuthResponse } from '../types'

export function useLogin() {
  return useMutation({
    // The device header says which device of the person this browser is, if it holds their keys, so the new session is tied to it.
    // The server ignores a device that is not the person's.
    mutationFn: async (data: LoginRequest) =>
      (await api.post<AuthResponse>('/auth/login', data, { headers: await deviceHeaders() })).data,
  })
}
