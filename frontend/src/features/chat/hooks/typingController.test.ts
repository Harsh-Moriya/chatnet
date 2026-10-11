import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createTypingController, TYPING_IDLE_MS, TYPING_REFRESH_MS } from './typingController'

// Fake timers also fake Date.now(), so "time passes" is just advancing the clock.
beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

function setup() {
  const sent: boolean[] = []
  const controller = createTypingController((isTyping) => {
    sent.push(isTyping)
  })
  return { controller, sent }
}

describe('typing controller', () => {
  it('tells the server as soon as the person starts typing', () => {
    const { controller, sent } = setup()

    controller.input('h')

    expect(sent).toEqual([true])
  })

  it('does not repeat itself on every keystroke', () => {
    const { controller, sent } = setup()

    controller.input('h')
    controller.input('he')
    controller.input('hel')
    controller.input('hell')

    expect(sent).toEqual([true])
  })

  it('repeats the signal while typing continues, so the other side keeps showing it', () => {
    const { controller, sent } = setup()

    controller.input('h')
    vi.advanceTimersByTime(TYPING_REFRESH_MS - 1)
    controller.input('he')
    expect(sent).toEqual([true])

    vi.advanceTimersByTime(1)
    controller.input('hel')
    expect(sent).toEqual([true, true])
  })

  it('says they stopped after a pause in typing', () => {
    const { controller, sent } = setup()
    controller.input('h')

    vi.advanceTimersByTime(TYPING_IDLE_MS - 1)
    expect(sent).toEqual([true])

    vi.advanceTimersByTime(1)
    expect(sent).toEqual([true, false])
  })

  it('keeps postponing the stop while the person keeps typing', () => {
    const { controller, sent } = setup()
    controller.input('h')

    for (let i = 0; i < 5; i++) {
      vi.advanceTimersByTime(TYPING_IDLE_MS - 500)
      controller.input('more')
    }

    expect(sent.filter((s) => !s)).toEqual([])
  })

  it('says they stopped when the text is cleared', () => {
    const { controller, sent } = setup()
    controller.input('h')

    controller.input('')

    expect(sent).toEqual([true, false])
  })

  it('treats a box of only spaces as not typing', () => {
    const { controller, sent } = setup()

    controller.input('   ')
    expect(sent).toEqual([])

    controller.input('h')
    controller.input('   ')
    expect(sent).toEqual([true, false])
  })

  it('says nothing when stopping without having started', () => {
    const { controller, sent } = setup()

    controller.stop()
    controller.input('')

    expect(sent).toEqual([])
  })

  it('says it only once when stopped twice', () => {
    const { controller, sent } = setup()
    controller.input('h')

    controller.stop()
    controller.stop()

    expect(sent).toEqual([true, false])
  })

  it('does not send a second stop when the idle timer fires after an explicit stop', () => {
    const { controller, sent } = setup()
    controller.input('h')
    controller.stop()

    vi.advanceTimersByTime(TYPING_IDLE_MS * 2)

    expect(sent).toEqual([true, false])
  })

  it('starts again if the person types after stopping', () => {
    const { controller, sent } = setup()
    controller.input('h')
    controller.stop()

    controller.input('hi')

    expect(sent).toEqual([true, false, true])
  })

  it('starts again if the person types after the idle stop', () => {
    const { controller, sent } = setup()
    controller.input('h')
    vi.advanceTimersByTime(TYPING_IDLE_MS)

    controller.input('hi')

    expect(sent).toEqual([true, false, true])
  })

  it('can be disposed, which stops and cancels the timer', () => {
    const { controller, sent } = setup()
    controller.input('h')

    controller.dispose()
    vi.advanceTimersByTime(TYPING_IDLE_MS * 2)

    expect(sent).toEqual([true, false])
  })
})
