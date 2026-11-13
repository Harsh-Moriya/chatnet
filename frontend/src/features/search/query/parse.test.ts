import { describe, expect, it } from 'vitest'
import { parseQuery, type Query } from './parse'

// What a person types, as the terms it asks for: the syntax the server's search had.

const terms = (query: Query | null) => query?.groups.map((group) => group.map((term) => `${term.negated ? '-' : ''}${term.words.join(' ')}${term.prefix ? '*' : ''}`))

describe('words', () => {
  it('is all prefix for a single word, because the person may not have finished typing it', () => {
    expect(terms(parseQuery('repo'))).toEqual([['repo*']])
  })

  it('makes only the last word a prefix', () => {
    expect(terms(parseQuery('  budget meeting repo '))).toEqual([['budget'], ['meeting'], ['repo*']])
  })

  it('ignores case and accents, as the messages are', () => {
    expect(terms(parseQuery('CAFÉ Crème'))).toEqual([['cafe'], ['creme*']])
  })

  it('reads a word with a hyphen or an apostrophe as the words it joins, standing together', () => {
    expect(terms(parseQuery('e-mail'))).toEqual([['e mail*']])
    expect(terms(parseQuery("don't"))).toEqual([['don t*']])
  })

  it('reads digits, underscores and other alphabets as words', () => {
    expect(terms(parseQuery('привет отчёт2'))).toEqual([['привет'], ['отчет2*']])
    expect(terms(parseQuery('snake_case'))).toEqual([['snake_case*']])
  })

  it('has no query for text with nothing to look for in it', () => {
    for (const nothing of ['', '   ', '!!', '-', '""', ' ... ', '"  "']) expect(parseQuery(nothing)).toBeNull()
  })
})

describe('a phrase in quotes', () => {
  it('is the words together, and exact', () => {
    expect(terms(parseQuery('"budget meeting"'))).toEqual([['budget meeting']])
  })

  it('is exact even when it is the last thing typed', () => {
    expect(terms(parseQuery('plan "big meeting"'))).toEqual([['plan'], ['big meeting']])
  })

  it('takes the rest of the text when it is not closed', () => {
    expect(terms(parseQuery('plan "big meeting'))).toEqual([['plan'], ['big meeting']])
  })

  it('does not make a prefix of the word before it, which is not the last thing typed', () => {
    expect(terms(parseQuery('plan "big"'))).toEqual([['plan'], ['big']])
  })
})

describe('a word to keep out', () => {
  it('is exact and not a prefix', () => {
    expect(terms(parseQuery('budget -dra'))).toEqual([['budget'], ['-dra']])
  })

  it('does not make a prefix of the word before it either, because the text does not end in it', () => {
    expect(terms(parseQuery('budget -draft'))?.[0]).toEqual(['budget'])
  })

  it('can be a phrase', () => {
    expect(terms(parseQuery('budget -"first draft"'))).toEqual([['budget'], ['-first draft']])
  })

  it('is not a query by itself, since there is nothing to find', () => {
    expect(parseQuery('-draft')).toBeNull()
    expect(parseQuery('-draft -old')).toBeNull()
  })

  it('is not a minus that is part of a word, nor one alone', () => {
    expect(terms(parseQuery('foo-ba'))).toEqual([['foo ba*']])
    expect(terms(parseQuery('budget -'))).toEqual([['budget']])
  })
})

describe('either of two', () => {
  it('puts the two in one group, and the word after "or" is exact', () => {
    expect(terms(parseQuery('cat or dog'))).toEqual([['cat', 'dog']])
  })

  it('reads "or" in any case', () => {
    expect(terms(parseQuery('cat OR dog'))).toEqual([['cat', 'dog']])
    expect(terms(parseQuery('budget cat Or dog'))).toEqual([['budget'], ['cat', 'dog']])
  })

  it('takes a phrase as one of the two', () => {
    expect(terms(parseQuery('"two words" or dog'))).toEqual([['two words', 'dog']])
  })

  it('takes more than two', () => {
    expect(terms(parseQuery('a or b or c'))).toEqual([['a', 'b', 'c']])
  })

  it('takes a word to keep out as one of the two', () => {
    expect(terms(parseQuery('cat or -dog'))).toEqual([['cat', '-dog']])
  })

  it('does not take a word to keep out for "or" when the word is "or"', () => {
    expect(terms(parseQuery('cat -or'))).toEqual([['cat'], ['-or']])
  })

  it('joins only the word right after "or", and the next one stands alone', () => {
    expect(terms(parseQuery('cat or dog bird'))).toEqual([['cat', 'dog'], ['bird*']])
  })

  it('ignores an "or" with nothing before it', () => {
    expect(terms(parseQuery('or orange'))).toEqual([['orange*']])
  })

  it('takes an "or" with nothing after it for a word, and the last one typed', () => {
    expect(terms(parseQuery('cat or'))).toEqual([['cat'], ['or*']])
    expect(terms(parseQuery('or'))).toEqual([['or*']])
  })

  it('does not take a word that only looks like "or" for it', () => {
    expect(terms(parseQuery('cat color'))).toEqual([['cat'], ['color*']])
    expect(terms(parseQuery('cat for dog'))).toEqual([['cat'], ['for'], ['dog*']])
  })
})

describe('what ends the text', () => {
  it('leaves a word followed by a mark exact, since it is not the unfinished one', () => {
    expect(terms(parseQuery('repo!'))).toEqual([['repo']])
  })

  it('leaves the word before a mark of its own exact', () => {
    expect(terms(parseQuery('budget ...'))).toEqual([['budget']])
  })
})
