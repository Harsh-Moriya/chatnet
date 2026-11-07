import { describe, expect, it } from 'vitest'
import { readResetToken } from './resetToken'

// What the server makes: 32 random bytes in base64url, which is 43 characters.
const token = 'Hq3v9Zx0-aB_cD1eF2gH3iJ4kL5mN6oP7qR8sT9uV0w'

describe('readResetToken', () => {
  it('reads the token from the fragment of the link in the email', () => {
    expect(token).toHaveLength(43)
    expect(readResetToken(`#token=${token}`)).toBe(token)
  })

  it('reads it with or without the leading hash', () => {
    expect(readResetToken(`token=${token}`)).toBe(token)
  })

  it('finds nothing when there is no fragment, or it has no token', () => {
    expect(readResetToken('')).toBeNull()
    expect(readResetToken('#')).toBeNull()
    expect(readResetToken('#other=1')).toBeNull()
    expect(readResetToken('#token=')).toBeNull()
  })

  it('refuses a token that cannot be one: another length, or characters that are not base64url', () => {
    expect(readResetToken(`#token=${token.slice(1)}`)).toBeNull()
    expect(readResetToken(`#token=${token}a`)).toBeNull()
    expect(readResetToken(`#token=${token.slice(0, 42)}+`)).toBeNull()
    expect(readResetToken(`#token=${token.slice(0, 42)}=`)).toBeNull()
    expect(readResetToken(`#token=${token.slice(0, 42)}%20`)).toBeNull()
  })

  it('reads the token among other parameters', () => {
    expect(readResetToken(`#utm=1&token=${token}`)).toBe(token)
  })
})
