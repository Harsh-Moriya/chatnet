// How text becomes words, for the messages that are stored and for what is searched for: the same function on both sides, so a word
// is found when it reads the same however it was written. Case and accents do not matter ("Café" is found by "cafe"), and a word is
// a run of letters, digits and underscores in any script. Punctuation and spaces only separate words.

const WORD = /[\p{L}\p{N}_]+/gu
const MARKS = /\p{M}/gu

// A word with its accents and case taken away. NFKD splits "é" into "e" and a combining mark, and a ligature or a wide form into the
// letters it stands for, so what remains after the marks are removed is what a person would type.
export function normalize(word: string): string {
  return word.normalize('NFKD').replace(MARKS, '').toLowerCase()
}

// A word as it appears in a text, with where it is, so a snippet can be cut around it and its place picked out.
export interface Token {
  word: string
  start: number
  end: number
}

export function tokenize(text: string): Token[] {
  const tokens: Token[] = []

  for (const found of text.matchAll(WORD)) {
    const word = normalize(found[0])
    if (word !== '') tokens.push({ word, start: found.index, end: found.index + found[0].length })
  }

  return tokens
}

// The words of a text, in order, which is what a message is stored with.
export function wordsOf(text: string): string[] {
  return tokenize(text).map((token) => token.word)
}
