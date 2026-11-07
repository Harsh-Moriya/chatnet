import { afterEach, describe, expect, it, vi } from 'vitest'
import type { AxiosResponse, InternalAxiosRequestConfig } from 'axios'
import { anonymousApi } from '../../../lib/anonymousApi'
import { requestPasswordReset, resetPassword } from './passwordResetApi'

afterEach(() => {
  vi.restoreAllMocks()
})

describe('the password reset calls', () => {
  it('asks for a link with the address, through the client that carries no token and no cookie', async () => {
    const post = vi.spyOn(anonymousApi, 'post').mockResolvedValue({ data: '' } as AxiosResponse)

    await requestPasswordReset('alice@example.test')

    expect(post).toHaveBeenCalledWith('/auth/forgot-password', { email: 'alice@example.test' })
  })

  it('sets the new password with the token from the link', async () => {
    const post = vi.spyOn(anonymousApi, 'post').mockResolvedValue({ data: '' } as AxiosResponse)

    await resetPassword('the-token', 'Brand1NewPassword')

    expect(post).toHaveBeenCalledWith('/auth/reset-password', { token: 'the-token', newPassword: 'Brand1NewPassword' })
  })

  it('lets a refusal through to the caller', async () => {
    vi.spyOn(anonymousApi, 'post').mockRejectedValue(new Error('refused'))

    await expect(resetPassword('t', 'p')).rejects.toThrow('refused')
  })

  // These calls must not renew or end the session of whoever is signed in on this browser, which a client with the token
  // interceptor or the cookie would do. What goes on the wire is what is checked, through an adapter that sends nothing.
  it('sends no authorization header and no credentials', async () => {
    let sent: InternalAxiosRequestConfig | null = null

    await anonymousApi.post('/auth/forgot-password', { email: 'a@b.test' }, {
      adapter: async (config) => {
        sent = config
        return { data: '', status: 202, statusText: 'Accepted', headers: {}, config }
      },
    })

    expect(sent).not.toBeNull()
    expect(sent!.withCredentials).toBeFalsy()
    expect(sent!.headers.Authorization).toBeUndefined()
    expect(sent!.baseURL).toBe('/api')
  })
})
