import { describe, expect, it } from 'vitest'
import { queryHolds, termMatches } from './match'
import { wordsOf } from './normalize'
import { parseQuery } from './parse'

// The query typed, against the text of a message, the way a search tests each message.
const finds = (query: string, text: string): boolean => queryHolds(wordsOf(text), parseQuery(query)!)

describe('the words of a term that stand together', () => {
  it('lets only the last of them be the start of a longer word', () => {
    // "e-mail" is the words e and mail, standing together, and mail may be unfinished: an "ex" in front of it is not an "e".
    expect(finds('e-mail', 'an ex mailbox')).toBe(false)
    expect(finds('e-mail', 'an e mailbox')).toBe(true)
  })
})

describe('one word', () => {
  it('is found anywhere in the text, in any case', () => {
    expect(finds('budget', 'The Budget is ready')).toBe(true)
  })

  it('is not found in a longer word, unless it is the unfinished one', () => {
    expect(finds('budget plan', 'budgets and plan')).toBe(false)
    expect(finds('plan budget', 'budgets and plan')).toBe(true)
  })

  it('is found by its start when it is the last thing typed', () => {
    expect(finds('repo', 'the report is in')).toBe(true)
    expect(finds('repo', 'a deposit')).toBe(false)
  })

  it('is not found by its end or its middle', () => {
    expect(finds('port', 'the report is in')).toBe(false)
  })

  it('is found without its accents, and with them', () => {
    expect(finds('cafe', 'Meet at the Café')).toBe(true)
    expect(finds('café', 'meet at the cafe')).toBe(true)
  })

  it('is not found in a text that lacks it', () => {
    expect(finds('xylophone', 'nothing of the kind')).toBe(false)
  })
})

describe('several words', () => {
  it('all have to be there, in any order', () => {
    expect(finds('meeting notes', 'notes from the meeting')).toBe(true)
    expect(finds('meeting notes', 'only the meeting')).toBe(false)
  })

  it('may be far apart', () => {
    expect(finds('red blue', 'red then green then yellow then blue')).toBe(true)
  })
})

describe('a phrase', () => {
  it('is the words one after the other', () => {
    expect(finds('"big meeting"', 'a big meeting today')).toBe(true)
  })

  it('is not the words apart or the wrong way round', () => {
    expect(finds('"big meeting"', 'a big long meeting')).toBe(false)
    expect(finds('"big meeting"', 'the meeting was big')).toBe(false)
  })

  it('is found across marks, which only separate words', () => {
    expect(finds('"big meeting"', 'a big, meeting')).toBe(true)
  })

  it('is exact to its last word, which is not an unfinished one', () => {
    expect(finds('"big meet"', 'a big meeting')).toBe(false)
  })

  it('is found by the words of a hyphenated word, standing together', () => {
    expect(finds('e-mail', 'send an e-mail soon')).toBe(true)
    expect(finds('e-mail', 'send an email soon')).toBe(false)
  })

  it('can start a text and can end one', () => {
    expect(finds('"one two"', 'one two')).toBe(true)
    expect(finds('"one two"', 'x one two')).toBe(true)
  })

  it('finds a repeated start before the real one', () => {
    expect(finds('"a a b"', 'a a a b')).toBe(true)
  })
})

describe('a word to keep out', () => {
  it('rules out a text that has it', () => {
    expect(finds('budget -draft', 'the budget draft')).toBe(false)
    expect(finds('budget -draft', 'the final budget')).toBe(true)
  })

  it('is exact, so a longer word does not rule a text out', () => {
    expect(finds('budget -dra', 'the budget draft')).toBe(true)
  })

  it('can be a phrase', () => {
    expect(finds('budget -"first draft"', 'the first draft of the budget')).toBe(false)
    expect(finds('budget -"first draft"', 'a draft of the budget, the first')).toBe(true)
  })
})

describe('either of two', () => {
  it('is found with either', () => {
    expect(finds('cat or dog', 'a cat')).toBe(true)
    expect(finds('cat or dog', 'a dog')).toBe(true)
    expect(finds('cat or dog', 'a bird')).toBe(false)
  })

  it('still needs the rest', () => {
    expect(finds('pets cat or dog', 'a cat')).toBe(false)
    expect(finds('pets cat or dog', 'pets: a dog')).toBe(true)
  })

  it('is exact for the word after "or"', () => {
    expect(finds('cat or dog', 'a doghouse')).toBe(false)
  })

  it('may be a word to keep out', () => {
    expect(finds('cat or -dog', 'a bird')).toBe(true)
    expect(finds('cat or -dog', 'a dog')).toBe(false)
    expect(finds('cat or -dog', 'a cat and a dog')).toBe(true)
  })
})

describe('terms on their own', () => {
  it('finds a term in an empty text for nothing', () => {
    expect(termMatches([], { words: ['a'], prefix: true, negated: false })).toBe(false)
  })

  it('finds a phrase longer than the text for nothing', () => {
    expect(termMatches(['a'], { words: ['a', 'b'], prefix: false, negated: false })).toBe(false)
  })
})
