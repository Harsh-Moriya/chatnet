import { CryptoError } from '../../lib/crypto'
import { getApiError } from '../../lib/errors'
import {
  InvalidLinkCodeError,
  KeysMismatchError,
  LinkEndedError,
  LinkKeyMismatchError,
  SetUpIncompleteError,
  isWrongPassphrase,
} from './flows'

// What to tell a person when one of the steps fails. One place, so every screen explains the same failure the same way. The
// passphrase, the keys and the codes are never part of a message.
export function describeFailure(failure: unknown): string {
  if (isWrongPassphrase(failure)) return 'That passphrase did not open your backup. Check it and try again.'

  if (failure instanceof SetUpIncompleteError) {
    return 'Your keys were created, but this browser could not be added as a device. Reload and enter your passphrase to finish.'
  }

  if (failure instanceof KeysMismatchError) {
    return 'The keys do not match the ones the server holds. Nothing was saved. If you changed your passphrase or keys recently, try again from your newest device.'
  }

  if (failure instanceof InvalidLinkCodeError) return 'That is not a link code. It has 10 letters and digits.'

  if (failure instanceof LinkKeyMismatchError) {
    return 'The request does not match the code you typed, so nothing was approved. Start again on the new device.'
  }

  if (failure instanceof LinkEndedError) {
    return failure.reason === 'expired'
      ? 'The request expired. Start again to get a new code.'
      : 'The keys were already handed over. If this browser does not have them, start again.'
  }

  if (failure instanceof CryptoError) {
    return failure.code === 'unsupported'
      ? 'This was made by a newer version of the app. Reload the page and try again.'
      : 'The data could not be read.'
  }

  return getApiError(failure)
}
