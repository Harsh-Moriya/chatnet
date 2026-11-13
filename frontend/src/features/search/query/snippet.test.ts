import { describe, expect, it } from 'vitest'
import { parseQuery } from './parse'
import { snippetOf } from './snippet'

const parts = (text: string, query: string) => snippetOf(text, parseQuery(query)!)

// The pieces as they would be read: the matching ones in brackets.
const shown = (text: string, query: string) =>
  parts(text, query)
    .map((part) => (part.match ? `[${part.text}]` : part.text))
    .join('')

describe('the text around a match', () => {
  it('picks out the matching word and leaves the rest as it was written', () => {
    expect(shown('Meet me at the Café tomorrow', 'cafe')).toBe('Meet me at the [Café] tomorrow')
  })

  it('picks out every word of the search, wherever it is', () => {
    expect(shown('red then green then blue', 'red blue')).toBe('[red] then green then [blue]')
  })

  it('picks out the start of a word the person has not finished typing', () => {
    expect(shown('the report is ready', 'repo')).toBe('the [report] is ready')
  })

  it('picks out each of the words of a phrase', () => {
    expect(shown('a big meeting today', '"big meeting"')).toBe('a [big] [meeting] today')
  })

  it('does not pick out a word the search keeps out', () => {
    expect(shown('the budget draft', 'budget -draft')).toBe('the [budget] draft')
  })

  it('picks out a word that is one of two', () => {
    expect(shown('a cat and a dog', 'cat or dog')).toBe('a [cat] and a [dog]')
  })

  it('picks out each occurrence, not only the first', () => {
    expect(shown('go go go', 'go')).toBe('[go] [go] [go]')
  })

  it('picks out a word in a text that is the other alphabet or has accents', () => {
    expect(shown('Привет, мир', 'привет')).toBe('[Привет], мир')
  })
})

describe('how much of the text is shown', () => {
  const long = (before: number, word: string, after: number) => `${'a'.repeat(before)} ${word} ${'b'.repeat(after)}`

  it('shows a short text whole, with no marks of being cut', () => {
    expect(shown('a short text with needle in it', 'needle')).toBe('a short text with [needle] in it')
  })

  it('cuts a long start, and says so', () => {
    const text = `${'x '.repeat(100)}needle here`

    const result = shown(text, 'needle')

    expect(result.startsWith('…')).toBe(true)
    expect(result).toContain('[needle] here')
    expect(result.length).toBeLessThan(text.length)
  })

  it('cuts a long end, and says so', () => {
    const text = `needle ${'y '.repeat(200)}`

    const result = shown(text, 'needle')

    expect(result.startsWith('[needle]')).toBe(true)
    expect(result.endsWith('…')).toBe(true)
  })

  it('does not say the start was cut when the match is near it', () => {
    expect(shown(long(10, 'needle', 400), 'needle').startsWith('…')).toBe(false)
  })

  it('does not say the end was cut when the text ends inside what is shown', () => {
    expect(shown(long(300, 'needle', 20), 'needle').endsWith('…')).toBe(false)
  })

  it('shows what is around the first match', () => {
    const text = `${'x '.repeat(100)}needle ${'z '.repeat(100)} needle`

    expect(shown(text, 'needle')).toContain('[needle]')
    expect(shown(text, 'needle').indexOf('z')).toBeGreaterThan(-1)
  })

  it('shows the start of a text with nothing to pick out', () => {
    const text = 'a text in which the thing is found by the name of a file'

    expect(parts(text, 'nowhere')).toEqual([{ text, match: false }])
  })

  it('shows only the start of a long text with nothing to pick out', () => {
    const text = 'word '.repeat(100)

    const result = parts(text, 'nowhere')

    expect(result).toHaveLength(2)
    expect(result[1]).toEqual({ text: '…', match: false })
  })
})

describe('exactly how much is shown', () => {
  // A match that begins at the 100th character of the text, so there is room to cut on both sides.
  const text = `${'a'.repeat(99)} needle ${'b'.repeat(300)}`
  const [start, before, hit, after, end] = parts(text, 'needle')

  it('shows 40 characters before the match, after a mark that the start was cut', () => {
    expect(start).toEqual({ text: '…', match: false })
    expect(before.text).toBe(`${'a'.repeat(39)} `)
  })

  it('shows the match and what follows it up to 120 characters from where it begins, and says the end was cut', () => {
    expect(hit).toEqual({ text: 'needle', match: true })
    expect(hit.text.length + after.text.length).toBe(120)
    expect(end).toEqual({ text: '…', match: false })
  })

  it('picks out only what is shown of a word that runs past the end of it', () => {
    const run = `needle ${'q'.repeat(300)}`

    const shownText = parts(run, 'needle q')
      .map((part) => part.text)
      .join('')

    expect(shownText).toBe(`needle ${'q'.repeat(113)}…`)
  })
})

describe('which words are picked out', () => {
  it('picks out only the last word of a term as the start of a longer one', () => {
    expect(shown('an ex e mailbox', 'e-mail')).toBe('an ex [e] [mailbox]')
  })

  it('picks out only a whole word for a word that is not the last typed', () => {
    expect(shown('category dog dogs', 'cat dog')).toBe('category [dog] [dogs]')
  })
})

describe('what is drawn', () => {
  it('turns every kind of white space into a space, so the text stays on its lines', () => {
    expect(shown('a\n\n  b\tneedle', 'needle')).toBe('a b [needle]')
  })

  it('is plain text and never markup: what looks like a tag is just characters', () => {
    const text = '<img src=x onerror=alert(1)> needle <script>x</script>'

    const result = parts(text, 'needle')

    expect(result.map((part) => part.text).join('')).toBe(text)
    expect(result.find((part) => part.match)?.text).toBe('needle')
  })

  it('does not lose or invent any text between the marks of being cut', () => {
    const text = `start ${'q '.repeat(80)}needle ${'r '.repeat(10)}`

    const joined = parts(text, 'needle')
      .map((part) => part.text)
      .join('')
      .replace(/…/g, '')

    expect(text.replace(/\s+/g, ' ')).toContain(joined.trim())
  })

  it('has no empty pieces', () => {
    expect(parts('needle', 'needle').every((part) => part.text !== '')).toBe(true)
  })
})
