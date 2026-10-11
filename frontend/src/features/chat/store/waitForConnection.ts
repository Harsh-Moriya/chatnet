import { useChatStore } from './chatStore'

// Resolves once the live connection is ready, and rejects if the request is cancelled first.
//
// Requests that load chat data (the conversation list, a conversation's history) wait here before
// they start. The server puts a connection into its conversation groups when it connects, and a
// message sent after the data was loaded but before that finished would never reach this client.
// Loading only after the connection is ready closes that gap. After a reconnect the same wait
// keeps stale requests from starting while the connection is down.
export function waitForConnection(signal?: AbortSignal): Promise<void> {
  if (signal?.aborted) return Promise.reject(signal.reason)
  if (useChatStore.getState().connection === 'connected') return Promise.resolve()

  return new Promise<void>((resolve, reject) => {
    const finish = () => {
      unsubscribe()
      signal?.removeEventListener('abort', onAbort)
    }

    const onAbort = () => {
      finish()
      reject(signal?.reason)
    }

    const unsubscribe = useChatStore.subscribe((state) => {
      if (state.connection === 'connected') {
        finish()
        resolve()
      }
    })

    signal?.addEventListener('abort', onAbort, { once: true })
  })
}
