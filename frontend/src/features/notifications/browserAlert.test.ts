import { describe, expect, it, vi } from 'vitest'
import type { AlertText } from './alerts'
import { type AlertHost, raiseAlert } from './browserAlert'

const text: AlertText = { title: 'Bob', body: 'hi', tag: 'conversation-c1', conversationId: 'c1', messageId: 'm1' }

function host(overrides: Partial<AlertHost> = {}) {
  const shown = { close: vi.fn(), onclick: null as (() => void) | null }
  const closeInList = vi.fn()
  const registration = {
    showNotification: vi.fn(async () => undefined),
    getNotifications: vi.fn(async () => [{ close: closeInList }]),
  }
  const real: AlertHost = {
    registration: async () => registration,
    create: vi.fn(() => shown),
    focus: vi.fn(),
    now: () => 5000,
    ...overrides,
  }
  return { real, shown, registration, closeInList }
}

const settle = () => new Promise((resolve) => setTimeout(resolve, 0))

describe('showing an alert in the page', () => {
  it('goes through the service worker when there is one, with the ids a click goes to', async () => {
    const { real, registration } = host()

    raiseAlert(real, text, () => {})
    await settle()

    expect(registration.showNotification).toHaveBeenCalledExactlyOnceWith(
      'Bob',
      expect.objectContaining({ body: 'hi', tag: 'conversation-c1', data: { conversationId: 'c1', messageId: 'm1' }, timestamp: 5000 }),
    )
    expect(real.create).not.toHaveBeenCalled()
  })

  it('shows it itself, and handles the click, when there is no service worker', async () => {
    const onOpen = vi.fn()
    const { real, shown } = host({ registration: async () => undefined })

    raiseAlert(real, text, onOpen)
    await settle()
    shown.onclick?.()

    expect(real.create).toHaveBeenCalledOnce()
    expect(shown.close).toHaveBeenCalledOnce()
    expect(real.focus).toHaveBeenCalledOnce()
    expect(onOpen).toHaveBeenCalledOnce()
  })

  it('shows it itself when the service worker cannot', async () => {
    const { real, registration } = host()
    registration.showNotification.mockRejectedValue(new Error('no'))

    raiseAlert(real, text, () => {})
    await settle()

    expect(real.create).toHaveBeenCalledOnce()
  })

  it('shows nothing, and does not fail, when the page cannot show one either (a constructor that throws, as on Android)', async () => {
    const { real } = host({
      registration: async () => undefined,
      create: () => {
        throw new TypeError('Illegal constructor')
      },
    })

    expect(() => raiseAlert(real, text, () => {})).not.toThrow()
    await settle()
  })

  it('takes a worker alert down by its tag', async () => {
    const { real, registration, closeInList } = host()

    const withdraw = raiseAlert(real, text, () => {})
    await settle()
    withdraw()
    await settle()

    expect(registration.getNotifications).toHaveBeenCalledWith({ tag: 'conversation-c1' })
    expect(closeInList).toHaveBeenCalledOnce()
  })

  it('takes a page alert down', async () => {
    const { real, shown } = host({ registration: async () => undefined })

    const withdraw = raiseAlert(real, text, () => {})
    await settle()
    withdraw()

    expect(shown.close).toHaveBeenCalledOnce()
  })

  it('takes an alert down that was withdrawn before it was shown, as soon as it is', async () => {
    const { real, registration, closeInList } = host()

    const withdraw = raiseAlert(real, text, () => {})
    withdraw()
    await settle()
    await settle()

    expect(registration.showNotification).toHaveBeenCalledOnce()
    expect(closeInList).toHaveBeenCalledOnce()
  })

  it('keeps a call alert on the screen until it is dealt with', async () => {
    const { real, registration } = host()

    raiseAlert(real, { ...text, tag: 'call-k1', persistent: true }, () => {})
    await settle()

    expect(registration.showNotification).toHaveBeenCalledWith('Bob', expect.objectContaining({ requireInteraction: true }))
  })
})
