import { describe, expect, it } from 'vitest'
import { normalize, tokenize, wordsOf } from './normalize'

describe('normalising a word', () => {
  it('ignores case', () => {
    expect(normalize('HeLLo')).toBe('hello')
  })

  it('takes accents away', () => {
    expect(normalize('Café')).toBe('cafe')
    expect(normalize('naïve')).toBe('naive')
    expect(normalize('Ångström')).toBe('angstrom')
  })

  it('writes a ligature and a wide form as the letters they stand for', () => {
    expect(normalize('ﬁle')).toBe('file')
    expect(normalize('ＡＢＣ')).toBe('abc')
  })

  it('leaves letters of other scripts as they are, in lower case', () => {
    expect(normalize('Привет')).toBe('привет')
    expect(normalize('日本語')).toBe('日本語')
  })

  it('writes a word made by composing and the same word made by combining the same way', () => {
    expect(normalize('é')).toBe(normalize('é'))
  })
})

describe('the words of a text', () => {
  it('splits on spaces and marks, and keeps the order', () => {
    expect(wordsOf('Hello, brave new world!')).toEqual(['hello', 'brave', 'new', 'world'])
  })

  it('takes digits and underscores as part of a word', () => {
    expect(wordsOf('order 66 of snake_case')).toEqual(['order', '66', 'of', 'snake_case'])
  })

  it('splits an apostrophe, a hyphen and an address into the words they join', () => {
    expect(wordsOf("don't e-mail a@b.com")).toEqual(['don', 't', 'e', 'mail', 'a', 'b', 'com'])
  })

  it('reads a run of letters of a script without spaces as one word', () => {
    expect(wordsOf('日本語のテキスト')).toEqual(['日本語のテキスト'])
  })

  it('has no words in nothing, or in marks alone', () => {
    expect(wordsOf('')).toEqual([])
    expect(wordsOf('  !?...  ')).toEqual([])
  })

  it('says the same of a text however its accents were written', () => {
    expect(wordsOf('Crème brûlée')).toEqual(wordsOf('Creme brulee'))
  })
})

describe('where the words are', () => {
  it('gives the start and the end of each word in the text as it was written', () => {
    const text = 'Café au lait'

    const tokens = tokenize(text)

    expect(tokens.map((token) => [token.word, text.slice(token.start, token.end)])).toEqual([
      ['cafe', 'Café'],
      ['au', 'au'],
      ['lait', 'lait'],
    ])
  })
})
