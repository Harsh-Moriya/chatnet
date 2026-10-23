import { describe, expect, it } from 'vitest'
import { activeMention, applyMention, MAX_SUGGESTIONS, splitMentions, suggestMembers } from './mentions'
import type { Member } from './types'

function member(userId: string, userName: string, displayName = userName): Member {
  return { userId, userName, displayName, avatarUrl: null, role: 'Member', lastReadAt: null }
}

const members = [member('u1', 'alice', 'Alice Smith'), member('u2', 'bob_99', 'Bob'), member('u3', 'carol', 'Carol')]

describe('splitMentions', () => {
  it('picks out a member named in the text', () => {
    expect(splitMentions('hi @alice, see this', members, 'bob_99')).toEqual([
      { text: 'hi ' },
      { text: '@alice', mention: { userName: 'alice', isSelf: false } },
      { text: ', see this' },
    ])
  })

  it('marks the one that names the reader', () => {
    const parts = splitMentions('@bob_99 hello', members, 'bob_99')

    expect(parts[0].mention).toEqual({ userName: 'bob_99', isSelf: true })
  })

  it('matches without regard to case, and keeps the member\x27s own spelling', () => {
    const parts = splitMentions('@ALICE', members, 'x')

    expect(parts[0]).toEqual({ text: '@ALICE', mention: { userName: 'alice', isSelf: false } })
  })

  it('finds several, and the same one twice', () => {
    const parts = splitMentions('@alice @carol @alice', members, 'x')

    expect(parts.filter((p) => p.mention).map((p) => p.text)).toEqual(['@alice', '@carol', '@alice'])
  })

  it('leaves a name that is nobody in the conversation as plain text', () => {
    expect(splitMentions('@nobody there', members, 'x')).toEqual([{ text: '@nobody there' }])
  })

  it('does not read an email address as a mention', () => {
    expect(splitMentions('write to alice@carol.com', members, 'x')).toEqual([{ text: 'write to alice@carol.com' }])
  })

  it('does not take a name that is only the start of a longer word', () => {
    expect(splitMentions('@alice_in_chains', members, 'x')).toEqual([{ text: '@alice_in_chains' }])
  })

  it('ends a name at punctuation', () => {
    const parts = splitMentions('thanks @carol.', members, 'x')

    expect(parts.map((p) => p.text)).toEqual(['thanks ', '@carol', '.'])
  })

  it('returns the text whole when the members are not known yet', () => {
    expect(splitMentions('@alice', undefined, 'x')).toEqual([{ text: '@alice' }])
    expect(splitMentions('@alice', [], 'x')).toEqual([{ text: '@alice' }])
  })

  it('returns an empty text as one empty part', () => {
    expect(splitMentions('', members, 'x')).toEqual([{ text: '' }])
  })

  it('keeps every character of the text', () => {
    const text = 'a @alice b @bob_99 c @zed d'

    expect(splitMentions(text, members, 'x').map((p) => p.text).join('')).toBe(text)
  })
})

describe('activeMention', () => {
  it('finds the name being typed at the cursor', () => {
    expect(activeMention('hello @al', 9)).toEqual({ start: 6, query: 'al' })
  })

  it('finds a bare at sign', () => {
    expect(activeMention('hello @', 7)).toEqual({ start: 6, query: '' })
  })

  it('finds a name at the start of the text', () => {
    expect(activeMention('@bo', 3)).toEqual({ start: 0, query: 'bo' })
  })

  it('looks only at the text before the cursor', () => {
    expect(activeMention('hello @alice and more', 9)).toEqual({ start: 6, query: 'al' })
  })

  it('is nothing once the name has ended with a space', () => {
    expect(activeMention('hello @alice ', 13)).toBeNull()
  })

  it('is nothing for an at sign inside a word', () => {
    expect(activeMention('mail alice@exam', 15)).toBeNull()
  })

  it('is nothing when there is no at sign', () => {
    expect(activeMention('hello', 5)).toBeNull()
  })

  it('is nothing for a name longer than a name can be', () => {
    expect(activeMention(`@${'a'.repeat(51)}`, 52)).toBeNull()
  })
})

describe('suggestMembers', () => {
  it('offers everyone but the reader for a bare at sign', () => {
    expect(suggestMembers(members, '', 'u2').map((m) => m.userId)).toEqual(['u1', 'u3'])
  })

  it('offers names that start with what was typed', () => {
    expect(suggestMembers(members, 'ca', 'x').map((m) => m.userName)).toEqual(['carol'])
  })

  it('matches without regard to case', () => {
    expect(suggestMembers(members, 'AL', 'x').map((m) => m.userName)).toEqual(['alice'])
  })

  it('also matches the display name', () => {
    expect(suggestMembers(members, 'smi', 'x').map((m) => m.userName)).toEqual(['alice'])
  })

  it('puts a user name that starts with it before a display name that starts with it before one that contains it', () => {
    const people = [
      member('1', 'zed', 'Xavier Alba'),
      member('2', 'cal', 'Calvin'),
      member('3', 'al_zz', 'Someone'),
      member('4', 'aaa', 'Alan'),
    ]

    expect(suggestMembers(people, 'al', 'x').map((m) => m.userId)).toEqual(['3', '4', '2', '1'])
  })

  it('offers nobody when nothing fits', () => {
    expect(suggestMembers(members, 'zzz', 'x')).toEqual([])
  })

  it('offers nobody when the members are not known yet', () => {
    expect(suggestMembers(undefined, 'a', 'x')).toEqual([])
  })

  it('offers at most the limit, in a stable order', () => {
    const many = Array.from({ length: 12 }, (_, i) => member(`u${i}`, `user_${String(i).padStart(2, '0')}`))

    const offered = suggestMembers(many, 'user', 'x')

    expect(offered).toHaveLength(MAX_SUGGESTIONS)
    expect(offered.map((m) => m.userName)).toEqual(many.slice(0, MAX_SUGGESTIONS).map((m) => m.userName))
  })
})

describe('applyMention', () => {
  it('replaces what was typed with the name and a space, and puts the cursor after it', () => {
    const text = 'hello @al'
    const mention = activeMention(text, text.length)!

    expect(applyMention(text, mention, text.length, 'alice')).toEqual({ text: 'hello @alice ', caret: 13 })
  })

  it('works in the middle of the text', () => {
    const text = 'hi @al, welcome'
    const mention = activeMention(text, 6)!

    expect(applyMention(text, mention, 6, 'alice')).toEqual({ text: 'hi @alice , welcome', caret: 10 })
  })

  it('does not add a second space when one follows', () => {
    const text = 'hi @al welcome'
    const mention = activeMention(text, 6)!

    expect(applyMention(text, mention, 6, 'alice')).toEqual({ text: 'hi @alice welcome', caret: 10 })
  })

  it('works for a bare at sign', () => {
    const mention = activeMention('@', 1)!

    expect(applyMention('@', mention, 1, 'carol')).toEqual({ text: '@carol ', caret: 7 })
  })

  it('writes something the server reads as a mention of the same person', () => {
    const mention = activeMention('@bo', 3)!
    const { text } = applyMention('@bo', mention, 3, 'bob_99')

    expect(splitMentions(text, members, 'x')[0].mention?.userName).toBe('bob_99')
  })
})
