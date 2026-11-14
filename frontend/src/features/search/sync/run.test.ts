import { IDBFactory, IDBKeyRange } from 'fake-indexeddb'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ANA } from '../testKit'
import type { SyncDeps } from './engine'
import type { Listed } from './plan'
import { readIntoStore } from './run'
import { useSyncStatus } from './status'

// The real wiring of a read, with the engine replaced by one the test controls, to see how runs are started and joined.
const syncAll = vi.fn<(deps: SyncDeps, listed: readonly Listed[]) => Promise<{ failed: string[] }>>()
vi.mock('./engine', () => ({ syncAll: (deps: SyncDeps, listed: readonly Listed[]) => syncAll(deps, listed) }))
vi.mock('../../chat/api/openWire', () => ({ openMessagesQuietly: vi.fn() }))
vi.mock('../api/changesApi', () => ({ fetchChanges: vi.fn(), fetchHistoryPage: vi.fn(), isCursorAhead: vi.fn() }))

beforeEach(() => {
  globalThis.indexedDB = new IDBFactory()
  globalThis.IDBKeyRange = IDBKeyRange
  syncAll.mockReset()
  useSyncStatus.getState().reset()
})

function gate() {
  let release!: () => void
  const wait = new Promise<void>((resolve) => (release = resolve))
  return { wait, release }
}

describe('starting to read into the store', () => {
  it('reads the list it was given, for the person, and passes the signal on', async () => {
    syncAll.mockResolvedValue({ failed: [] })
    const signal = new AbortController().signal

    await readIntoStore(ANA, [{ id: 'c1', changeSeq: 4 }], signal)

    expect(syncAll).toHaveBeenCalledTimes(1)
    expect(syncAll.mock.calls[0][0]).toMatchObject({ userId: ANA, signal })
    expect(syncAll.mock.calls[0][1]).toEqual([{ id: 'c1', changeSeq: 4 }])
  })

  it('tells the search how many conversations still have history to read', async () => {
    syncAll.mockImplementation(async (deps) => {
      deps.onHistoryPending?.(3)
      return { failed: [] }
    })

    await readIntoStore(ANA, [{ id: 'c1', changeSeq: 1 }], new AbortController().signal)

    expect(useSyncStatus.getState().historyPending).toBe(3)
  })

  it('does not start a second read while one is going, and reads the newest list when the first is done', async () => {
    const first = gate()
    syncAll.mockImplementationOnce(async () => {
      await first.wait
      return { failed: [] }
    })
    syncAll.mockResolvedValue({ failed: [] })
    const signal = new AbortController().signal

    const running = readIntoStore(ANA, [{ id: 'c1', changeSeq: 1 }], signal)
    await vi.waitFor(() => expect(syncAll).toHaveBeenCalledTimes(1))
    void readIntoStore(ANA, [{ id: 'c1', changeSeq: 2 }], signal)
    void readIntoStore(ANA, [{ id: 'c1', changeSeq: 3 }], signal)
    expect(syncAll).toHaveBeenCalledTimes(1)

    first.release()
    await running

    expect(syncAll).toHaveBeenCalledTimes(2)
    expect(syncAll.mock.calls[1][1]).toEqual([{ id: 'c1', changeSeq: 3 }])
  })

  it('can read again after a read has finished', async () => {
    syncAll.mockResolvedValue({ failed: [] })
    const signal = new AbortController().signal

    await readIntoStore(ANA, [{ id: 'c1', changeSeq: 1 }], signal)
    await readIntoStore(ANA, [{ id: 'c1', changeSeq: 2 }], signal)

    expect(syncAll).toHaveBeenCalledTimes(2)
  })

  it('does not read at all once it has been told to stop', async () => {
    const stop = new AbortController()
    stop.abort()

    await readIntoStore(ANA, [{ id: 'c1', changeSeq: 1 }], stop.signal)

    expect(syncAll).not.toHaveBeenCalled()
  })

  it('survives a read that failed, and does not report it as unavailable', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined)
    syncAll.mockRejectedValueOnce(new Error('the server is down'))
    syncAll.mockResolvedValue({ failed: [] })
    const signal = new AbortController().signal

    await readIntoStore(ANA, [{ id: 'c1', changeSeq: 1 }], signal)
    await readIntoStore(ANA, [{ id: 'c1', changeSeq: 2 }], signal)

    expect(syncAll).toHaveBeenCalledTimes(2)
    expect(useSyncStatus.getState().unavailable).toBe(false)
    expect(warn).toHaveBeenCalledWith(expect.any(String), 'the server is down')
    warn.mockRestore()
  })

  it('does not warn about a read that stopped because it was told to', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined)
    const stop = new AbortController()
    syncAll.mockImplementation(async () => {
      stop.abort()
      throw new DOMException('aborted', 'AbortError')
    })

    await readIntoStore(ANA, [{ id: 'c1', changeSeq: 1 }], stop.signal)

    expect(warn).not.toHaveBeenCalled()
    warn.mockRestore()
  })

  it('says search is not available where the browser cannot keep the store, and reads nothing', async () => {
    vi.stubGlobal('indexedDB', undefined)

    await readIntoStore(ANA, [{ id: 'c1', changeSeq: 1 }], new AbortController().signal)

    expect(useSyncStatus.getState().unavailable).toBe(true)
    expect(syncAll).not.toHaveBeenCalled()
  })
})
