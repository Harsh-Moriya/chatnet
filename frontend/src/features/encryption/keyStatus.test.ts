import { beforeEach, describe, expect, it } from 'vitest'
import { useKeyStatusStore } from './keyStatus'

const status = () => useKeyStatusStore.getState()

beforeEach(() => {
  status().reset()
})

describe('who a conversation is waiting for', () => {
  it('is nobody until a conversation is held up', () => {
    expect(status().waitingFor).toEqual({})
  })

  it('names the people who have not set up encryption, for the conversation they hold up', () => {
    status().setWaitingFor('c1', ['u1', 'u2'])
    status().setWaitingFor('c2', ['u3'])

    expect(status().waitingFor).toEqual({ c1: ['u1', 'u2'], c2: ['u3'] })
  })

  it('takes the latest account of a conversation in place of the one before', () => {
    status().setWaitingFor('c1', ['u1', 'u2'])
    status().setWaitingFor('c1', ['u2'])

    expect(status().waitingFor.c1).toEqual(['u2'])
  })

  it('lets a conversation go once it is ready, and leaves the others held up', () => {
    status().setWaitingFor('c1', ['u1'])
    status().setWaitingFor('c2', ['u3'])

    status().clear('c1')

    expect(status().waitingFor).toEqual({ c2: ['u3'] })
  })

  it('changes nothing when the conversation was not held up', () => {
    status().setWaitingFor('c2', ['u3'])
    const before = status().waitingFor

    status().clear('c1')

    expect(status().waitingFor).toBe(before)
  })

  it('forgets everything when the person signs out', () => {
    status().setWaitingFor('c1', ['u1'])

    status().reset()

    expect(status().waitingFor).toEqual({})
  })
})
