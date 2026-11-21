import { describe, expect, it } from 'vitest'
import { colourFor } from './avatarColour'

describe('the colour of a person who has no picture', () => {
  it('is always the same for the same name, so a person keeps their colour', () => {
    expect(colourFor('Ben Moore')).toBe(colourFor('Ben Moore'))
    expect(colourFor('')).toBe(colourFor(''))
  })

  it('has a dark pair for the light one, so it reads in both themes', () => {
    for (const name of ['Ana', 'Ben', 'Cleo', 'Dev', 'Eli', 'Fay', 'Gus', 'Hana']) {
      expect(colourFor(name)).toMatch(/^bg-\w+-100 text-\w+-700 dark:bg-\w+-900\/60 dark:text-\w+-200$/)
    }
  })

  it('spreads names over most of the palette instead of a few near-identical colours', () => {
    const names = Array.from({ length: 60 }, (_, index) => `Person ${index}`)

    expect(new Set(names.map(colourFor)).size).toBeGreaterThanOrEqual(6)
  })
})
