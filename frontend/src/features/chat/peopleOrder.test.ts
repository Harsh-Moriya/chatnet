import { describe, expect, it } from 'vitest'
import { contactIdsOf, newPeopleFirst } from './peopleOrder'
import type { Conversation, UserSummary } from './types'

const conversation = (id: string, type: 'Direct' | 'Group', otherUserId: string | null) => ({ id, type, otherUserId }) as Conversation
const person = (id: string, displayName: string): UserSummary => ({ id, userName: displayName.toLowerCase(), displayName, avatarUrl: null })

describe('who the signed-in user already chats with', () => {
  it('is the other person of every direct conversation', () => {
    const ids = contactIdsOf({ a: conversation('a', 'Direct', 'ben'), b: conversation('b', 'Direct', 'cleo') })

    expect([...ids].sort()).toEqual(['ben', 'cleo'])
  })

  it('leaves out groups, which have no one other person', () => {
    expect(contactIdsOf({ g: conversation('g', 'Group', null), h: conversation('h', 'Group', 'ben') }).size).toBe(0)
  })

  it('is nobody when there are no conversations', () => {
    expect(contactIdsOf({}).size).toBe(0)
  })
})

describe('putting people to start something with first', () => {
  const people = [person('1', 'Ana'), person('2', 'Ben'), person('3', 'Cleo'), person('4', 'Dev')]

  it('lists the people with no conversation first and the others after, each in the order given', () => {
    const ordered = newPeopleFirst(people, new Set(['1', '3']))

    expect(ordered.map((p) => p.displayName)).toEqual(['Ben', 'Dev', 'Ana', 'Cleo'])
  })

  it('keeps the order when nobody is a contact, or everybody is', () => {
    expect(newPeopleFirst(people, new Set()).map((p) => p.id)).toEqual(['1', '2', '3', '4'])
    expect(newPeopleFirst(people, new Set(['1', '2', '3', '4'])).map((p) => p.id)).toEqual(['1', '2', '3', '4'])
  })

  it('loses nobody and invents nobody', () => {
    expect(newPeopleFirst(people, new Set(['2'])).length).toBe(people.length)
    expect(newPeopleFirst([], new Set(['2']))).toEqual([])
  })
})
