import { describe, expect, it } from 'vitest'
import type { ReadPoint } from '../store/types'
import { planSync, signatureOf } from './plan'

const point = (conversationId: string, overrides: Partial<ReadPoint> = {}): ReadPoint => ({
  userId: 'u',
  conversationId,
  cursor: 5,
  startSeq: 5,
  oldestId: 'm1',
  historyRead: true,
  ...overrides,
})

describe('what the store needs from the server', () => {
  it('asks for the history of a conversation nothing was read of', () => {
    expect(planSync([{ id: 'c1', changeSeq: 12 }], [])).toEqual([{ id: 'c1', changeSeq: 12, history: true }])
  })

  it('asks for the history of one whose history was only part read', () => {
    expect(planSync([{ id: 'c1', changeSeq: 12 }], [point('c1', { historyRead: false, cursor: null })])).toEqual([
      { id: 'c1', changeSeq: 12, history: true },
    ])
  })

  it('asks only for what changed in a conversation whose history was read and that has moved', () => {
    expect(planSync([{ id: 'c1', changeSeq: 9 }], [point('c1', { cursor: 5 })])).toEqual([{ id: 'c1', changeSeq: 9, history: false }])
  })

  it('asks for nothing of a conversation that is where the list says', () => {
    expect(planSync([{ id: 'c1', changeSeq: 5 }], [point('c1', { cursor: 5 })])).toEqual([])
  })

  it('asks the feed when the cursor is past what the list says, which it will answer with a refusal if the server went back', () => {
    expect(planSync([{ id: 'c1', changeSeq: 3 }], [point('c1', { cursor: 5 })])).toEqual([{ id: 'c1', changeSeq: 3, history: false }])
  })

  it('keeps the order of the list, the most active first', () => {
    const plan = planSync(
      [
        { id: 'b', changeSeq: 1 },
        { id: 'a', changeSeq: 1 },
        { id: 'c', changeSeq: 1 },
      ],
      [],
    )

    expect(plan.map((work) => work.id)).toEqual(['b', 'a', 'c'])
  })

  it('does not take the read point of one conversation for another', () => {
    const plan = planSync(
      [
        { id: 'c1', changeSeq: 5 },
        { id: 'c2', changeSeq: 5 },
      ],
      [point('c1', { cursor: 5 })],
    )

    expect(plan).toEqual([{ id: 'c2', changeSeq: 5, history: true }])
  })

  it('has nothing to do for an empty list', () => {
    expect(planSync([], [point('c1')])).toEqual([])
  })
})

describe('knowing that the list moved', () => {
  it('is the same for the same list, whatever order it is in', () => {
    expect(signatureOf({ a: { changeSeq: 1 }, b: { changeSeq: 2 } })).toBe(signatureOf({ b: { changeSeq: 2 }, a: { changeSeq: 1 } }))
  })

  it('changes when the number of a conversation does', () => {
    expect(signatureOf({ a: { changeSeq: 1 } })).not.toBe(signatureOf({ a: { changeSeq: 2 } }))
  })

  it('changes when a conversation is added', () => {
    expect(signatureOf({ a: { changeSeq: 1 } })).not.toBe(signatureOf({ a: { changeSeq: 1 }, b: { changeSeq: 0 } }))
  })

  it('is empty for no conversations', () => {
    expect(signatureOf({})).toBe('')
  })
})
