import { getApiError } from '../../lib/errors'

// What a person is told when turning alerts on fails. The browser's own push API fails with a DOMException whose message can
// name the push service or the address it was given, so only its kind is used. The errors of the server are told as everywhere else.
export function pushErrorMessage(error: unknown): string {
  if (error instanceof DOMException) {
    switch (error.name) {
      // Chrome says this when it cannot register with its push service, and Brave until push messaging is allowed in its settings.
      case 'AbortError':
        return "The browser could not reach its push service. Check the connection and the browser's settings for push messaging, then try again."
      case 'NotAllowedError':
        return 'The browser did not allow alerts.'
      case 'NotSupportedError':
        return 'This browser cannot show alerts while the app is closed.'
    }
  }

  return getApiError(error)
}
