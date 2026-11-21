import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

const css = readFileSync(new URL('./index.css', import.meta.url), 'utf-8')

// What a device that asks for less motion must get: this is the only place that makes sure of it for every animation in the application,
// so a change that drops the rule, or one of its two halves, is caught here.
describe('reduced motion', () => {
  const start = css.indexOf('@media (prefers-reduced-motion: reduce)')
  const rule = start === -1 ? '' : css.slice(start)

  it('has a rule for a device that asks for less motion', () => {
    expect(start).toBeGreaterThan(-1)
  })

  it('ends every animation and transition at once', () => {
    expect(rule).toMatch(/animation-duration:\s*0\.01ms\s*!important/)
    expect(rule).toMatch(/animation-iteration-count:\s*1\s*!important/)
    expect(rule).toMatch(/transition-duration:\s*0\.01ms\s*!important/)
  })

  it('leaves only the spinner turning', () => {
    expect(rule).toContain(':not(.animate-spin)')
  })

  it('is after the animations it overrides, so a later rule cannot win over it', () => {
    expect(start).toBeGreaterThan(css.indexOf('--animate-fade'))
  })
})
