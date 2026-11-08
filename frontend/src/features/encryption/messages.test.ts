import { AxiosError } from 'axios'
import { describe, expect, it } from 'vitest'
import { CryptoError } from '../../lib/crypto'
import {
  InvalidLinkCodeError,
  KeysMismatchError,
  LinkEndedError,
  LinkKeyMismatchError,
  SetUpIncompleteError,
} from './flows'
import { describeFailure } from './messages'

const apiError = (status: number, detail?: string) =>
  new AxiosError('failed', 'ERR', undefined, undefined, {
    status,
    data: detail === undefined ? undefined : { detail },
    statusText: '',
    headers: {},
    config: { headers: {} } as never,
  })

describe('describeFailure', () => {
  it('tells a wrong passphrase from other failures', () => {
    expect(describeFailure(new CryptoError('authentication', 'x'))).toMatch(/passphrase did not open/)
  })

  it('explains a set-up that stopped half way, and how to finish it', () => {
    expect(describeFailure(new SetUpIncompleteError({ cause: new Error('x') }))).toMatch(/enter your passphrase to finish/)
  })

  it('explains each way a link can fail', () => {
    expect(describeFailure(new InvalidLinkCodeError())).toMatch(/10 letters and digits/)
    expect(describeFailure(new LinkKeyMismatchError())).toMatch(/nothing was approved/)
    expect(describeFailure(new LinkEndedError('expired'))).toMatch(/expired/)
    expect(describeFailure(new LinkEndedError('collected'))).toMatch(/already handed over/)
    expect(describeFailure(new KeysMismatchError())).toMatch(/Nothing was saved/)
  })

  it('does not call data from a newer version damaged', () => {
    expect(describeFailure(new CryptoError('unsupported', 'x'))).toMatch(/newer version/)
    expect(describeFailure(new CryptoError('malformed', 'x'))).toBe('The data could not be read.')
  })

  it('shows what the server said about a request it refused', () => {
    expect(describeFailure(apiError(404, 'No open request to link a device has this code.'))).toBe(
      'No open request to link a device has this code.',
    )
    expect(describeFailure(apiError(409, 'The backup was changed from another device. Fetch it again and retry.'))).toMatch(/changed from another device/)
  })

  it('falls back to the usual wording for everything else', () => {
    expect(describeFailure(apiError(500))).toBe('The server ran into a problem. Please try again.')
    expect(describeFailure(new Error('boom'))).toBe('An unexpected error occurred')
  })
})
