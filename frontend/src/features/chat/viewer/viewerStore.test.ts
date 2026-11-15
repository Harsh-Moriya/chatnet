import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useViewerStore } from './viewerStore'

beforeEach(() => {
  useViewerStore.setState({ target: null })
})

describe('the file the viewer is showing', () => {
  it('shows nothing until a file is opened', () => {
    expect(useViewerStore.getState().target).toBeNull()
  })

  it('shows the file it was asked to open', () => {
    useViewerStore.getState().open('c1', 'a1')

    expect(useViewerStore.getState().target).toEqual({ conversationId: 'c1', attachmentId: 'a1', from: 'chat' })
  })

  it('remembers that the file was opened from the gallery', () => {
    useViewerStore.getState().open('c1', 'a1', 'gallery')

    expect(useViewerStore.getState().target).toEqual({ conversationId: 'c1', attachmentId: 'a1', from: 'gallery' })
  })

  it('shows another file when it is asked to open one while it is open', () => {
    useViewerStore.getState().open('c1', 'a1')
    useViewerStore.getState().open('c1', 'a2')

    expect(useViewerStore.getState().target).toEqual({ conversationId: 'c1', attachmentId: 'a2', from: 'chat' })
  })

  it('shows nothing once closed', () => {
    useViewerStore.getState().open('c1', 'a1')
    useViewerStore.getState().close()

    expect(useViewerStore.getState().target).toBeNull()
  })

  it('tells nobody when it is closed while it is already closed', () => {
    const listener = vi.fn()
    const stop = useViewerStore.subscribe(listener)

    useViewerStore.getState().close()
    useViewerStore.getState().close()
    stop()

    expect(listener).not.toHaveBeenCalled()
  })
})
