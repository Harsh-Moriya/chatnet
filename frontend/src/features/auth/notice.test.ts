import { describe, expect, it } from 'vitest'
import { PASSWORD_CHANGED_STATE, wasPasswordChanged } from './notice'

describe('the password changed notice', () => {
  it('is recognised in the state the reset page navigates with', () => {
    expect(wasPasswordChanged(PASSWORD_CHANGED_STATE)).toBe(true)
  })

  it('is not recognised in any other state', () => {
    expect(wasPasswordChanged(null)).toBe(false)
    expect(wasPasswordChanged(undefined)).toBe(false)
    expect(wasPasswordChanged('passwordChanged')).toBe(false)
    expect(wasPasswordChanged({})).toBe(false)
    expect(wasPasswordChanged({ passwordChanged: 'yes' })).toBe(false)
    expect(wasPasswordChanged({ passwordChanged: false })).toBe(false)
  })
})
