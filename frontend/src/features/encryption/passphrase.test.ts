import { describe, expect, it } from 'vitest'
import { passphraseProblem } from './passphrase'

describe('passphraseProblem', () => {
  it('accepts twelve characters typed twice', () => {
    expect(passphraseProblem('twelve chars', 'twelve chars')).toBeNull()
  })

  it('asks for at least twelve characters', () => {
    expect(passphraseProblem('only eleven', 'only eleven')).toBe('Use at least 12 characters.')
    expect(passphraseProblem('', '')).toBe('Use at least 12 characters.')
  })

  it('asks for the same passphrase twice, and tells length first', () => {
    expect(passphraseProblem('twelve chars', 'twelve charz')).toBe('The two passphrases do not match.')
    expect(passphraseProblem('short', 'different')).toBe('Use at least 12 characters.')
  })

  // The length is counted the way the backup counts it: characters after normalisation, so a ligature that normalises to two
  // letters counts as two, and an emoji is one.
  it('counts characters as the backup does', () => {
    expect(passphraseProblem('ﬁ'.repeat(6), 'ﬁ'.repeat(6))).toBeNull()
    expect(passphraseProblem('😀'.repeat(11), '😀'.repeat(11))).toBe('Use at least 12 characters.')
    expect(passphraseProblem('😀'.repeat(12), '😀'.repeat(12))).toBeNull()
  })

  it('does not trim or fold what was typed', () => {
    expect(passphraseProblem('twelve chars ', 'twelve chars')).toBe('The two passphrases do not match.')
    expect(passphraseProblem('Twelve chars', 'twelve chars')).toBe('The two passphrases do not match.')
  })
})
