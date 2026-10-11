import { AxiosError, type AxiosResponse } from 'axios'
import { describe, expect, it } from 'vitest'
import { getApiError } from './errors'

function responseError(status: number, data: unknown): AxiosError {
  return new AxiosError(`Request failed with status code ${status}`, 'ERR_BAD_RESPONSE', undefined, undefined, {
    status,
    data,
  } as AxiosResponse)
}

// The message is shown to people, so it has to read like a sentence whatever went wrong.
describe('getApiError', () => {
  it('shows the detail the API sends', () => {
    expect(getApiError(responseError(403, { detail: 'You are not a member of this conversation.' }))).toBe(
      'You are not a member of this conversation.',
    )
  })

  it('does not show the library\'s wording for a server error that has no detail', () => {
    expect(getApiError(responseError(500, ''))).toBe('The server ran into a problem. Please try again.')
    expect(getApiError(responseError(502, null))).toBe('The server ran into a problem. Please try again.')
  })

  it('says the server cannot be reached when there was no response at all', () => {
    expect(getApiError(new AxiosError('Network Error', 'ERR_NETWORK'))).toBe(
      "Can't reach the server. Check your connection and try again.",
    )
    expect(getApiError(new AxiosError('timeout of 15000ms exceeded', 'ECONNABORTED'))).toBe(
      "Can't reach the server. Check your connection and try again.",
    )
  })

  it('prefers the API\'s detail even for a server error', () => {
    expect(getApiError(responseError(500, { detail: 'An unexpected error occurred. Please try again later.' }))).toBe(
      'An unexpected error occurred. Please try again later.',
    )
  })

  it('falls back to the library message for a client error without a detail', () => {
    expect(getApiError(responseError(404, ''))).toBe('Request failed with status code 404')
  })

  it('handles an error that did not come from a request', () => {
    expect(getApiError(new Error('boom'))).toBe('An unexpected error occurred')
    expect(getApiError('text')).toBe('An unexpected error occurred')
  })
})
