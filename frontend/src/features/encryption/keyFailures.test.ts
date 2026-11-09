import { describe, expect, it } from 'vitest'
import { IdentityChangedError } from './pins'
import { EpochContestedError, EpochRefused, MissingKeysError } from './epochs'
import { KeysMismatchError } from './flows'
import { describeKeyFailure } from './keyFailures'

const names: Record<string, string> = { a: 'Ana', b: 'Ben', c: 'Cy' }
const nameOf = (id: string) => names[id] ?? 'Someone'

describe('telling a person why a conversation could not be started', () => {
  it('names the one person who has not set up encryption', () => {
    expect(describeKeyFailure(new MissingKeysError(['b']), nameOf)).toBe(
      'Ben has not set up encryption yet. Ask them to sign in and finish setting it up, then try again.',
    )
  })

  it('names several people', () => {
    expect(describeKeyFailure(new MissingKeysError(['a', 'b', 'c']), nameOf)).toContain('Ana, Ben and Cy have not set up encryption yet.')
    expect(describeKeyFailure(new MissingKeysError(['a', 'b']), nameOf)).toContain('Ana and Ben have not set up encryption yet.')
  })

  it('does not know a person it was not told about', () => {
    expect(describeKeyFailure(new MissingKeysError(['zed']), nameOf)).toContain('Someone has not set up')
  })

  it('names the person whose security key changed', () => {
    expect(describeKeyFailure(new IdentityChangedError('c', new Uint8Array(32)), nameOf)).toContain('Cy has a different security key')
  })

  it('says someone has no keys when the server found out first, and to try again for the other refusals', () => {
    expect(describeKeyFailure(new EpochRefused('member-has-no-keys'), nameOf)).toBe('Someone in this conversation has not set up encryption yet.')
    expect(describeKeyFailure(new EpochRefused('epoch-stale'), nameOf)).toContain('Please try again.')
    expect(describeKeyFailure(new EpochRefused('epoch-members-changed'), nameOf)).toContain('Please try again.')
    expect(describeKeyFailure(new EpochContestedError(), nameOf)).toContain('Please try again.')
  })

  it('explains keys that do not match, and keys that are not here', () => {
    expect(describeKeyFailure(new KeysMismatchError(), nameOf)).toContain('not the ones on the server')
    const notReady = Object.assign(new Error('x'), { name: 'EncryptionNotReadyError' })
    expect(describeKeyFailure(notReady, nameOf)).toContain('not available on this browser')
  })

  it('has nothing to say about a failure that is not about keys', () => {
    expect(describeKeyFailure(new Error('Network Error'), nameOf)).toBeNull()
    expect(describeKeyFailure('a string', nameOf)).toBeNull()
    expect(describeKeyFailure(undefined, nameOf)).toBeNull()
  })
})
