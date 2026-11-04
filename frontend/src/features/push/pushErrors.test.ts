import { AxiosError } from 'axios'
import { describe, expect, it } from 'vitest'
import { pushErrorMessage } from './pushErrors'

describe('what a person is told when turning alerts on fails', () => {
  // Chrome reports this when it cannot register with Google's push service, and Brave does for every site until push messaging is
  // allowed in its settings.
  it('says the push service could not be reached when the browser aborts the subscription', () => {
    const message = pushErrorMessage(new DOMException('Registration failed - push service error', 'AbortError'))

    expect(message).toContain('push service')
    expect(message).not.toBe('An unexpected error occurred')
  })

  it('says the browser did not allow it when it refuses', () => {
    expect(pushErrorMessage(new DOMException('denied', 'NotAllowedError'))).toContain('did not allow')
  })

  it('says the browser cannot do it when it does not support it', () => {
    expect(pushErrorMessage(new DOMException('no push', 'NotSupportedError'))).toContain('cannot')
  })

  it('does not repeat what the browser said, which can name the push service', () => {
    const message = pushErrorMessage(new DOMException('Registration failed for https://fcm.googleapis.com/fcm/send/secret', 'AbortError'))

    expect(message).not.toContain('secret')
    expect(message).not.toContain('fcm.googleapis.com')
  })

  it('leaves the rest to the way an error of the server is told', () => {
    const failure = new AxiosError('Request failed', 'ERR_BAD_RESPONSE', undefined, undefined, {
      status: 400,
      statusText: 'Bad Request',
      headers: {},
      config: {} as never,
      data: { detail: 'The push address must be an https address of a push service this server allows.' },
    })

    expect(pushErrorMessage(failure)).toBe('The push address must be an https address of a push service this server allows.')
    expect(pushErrorMessage(new Error('boom'))).toBe('An unexpected error occurred')
  })

  it('treats another kind of browser error as an unexpected one', () => {
    expect(pushErrorMessage(new DOMException('x', 'SomethingElseError'))).toBe('An unexpected error occurred')
  })
})
