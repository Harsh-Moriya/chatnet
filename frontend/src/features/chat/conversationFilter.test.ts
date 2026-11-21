import { describe, expect, it } from 'vitest'
import { filterConversations } from './conversationFilter'

const list = [{ name: 'Bar Bravo' }, { name: 'José Álvarez' }, { name: 'Game night' }, { name: 'Ben Moore' }]
const names = (found: readonly { name: string }[]) => found.map((c) => c.name)

describe('filtering the conversations by name', () => {
  it('finds a name by a part of it, wherever it is in the name', () => {
    expect(names(filterConversations(list, 'bra'))).toEqual(['Bar Bravo'])
    expect(names(filterConversations(list, 'night'))).toEqual(['Game night'])
  })

  it('does not mind capitals', () => {
    expect(names(filterConversations(list, 'BEN'))).toEqual(['Ben Moore'])
    expect(names(filterConversations(list, 'game NIGHT'))).toEqual(['Game night'])
  })

  it('does not mind accents, in what is typed or in the name', () => {
    expect(names(filterConversations(list, 'jose'))).toEqual(['José Álvarez'])
    expect(names(filterConversations(list, 'álvarez'))).toEqual(['José Álvarez'])
    expect(names(filterConversations(list, 'alvarez'))).toEqual(['José Álvarez'])
  })

  it('ignores spaces at either end of what was typed, but not inside it', () => {
    expect(names(filterConversations(list, '  ben  '))).toEqual(['Ben Moore'])
    expect(names(filterConversations(list, 'bar  bravo'))).toEqual([])
  })

  it('is every conversation, as the same list, when nothing is typed', () => {
    expect(filterConversations(list, '')).toBe(list)
    expect(filterConversations(list, '   ')).toBe(list)
  })

  it('is none when nothing matches, and keeps the order of what does', () => {
    expect(filterConversations(list, 'zzz')).toEqual([])
    expect(names(filterConversations(list, 'b'))).toEqual(['Bar Bravo', 'Ben Moore'])
  })
})
