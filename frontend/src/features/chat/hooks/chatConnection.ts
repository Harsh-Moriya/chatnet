import { reconnectDelay } from '../../../lib/signalr'
import { useChatStore } from '../store/chatStore'
import type { Presence } from '../../presence/types'
import type { Conversation, Message } from '../types'

// Everything that connects the SignalR hub to the chat store, as plain functions with no React so it
// can be tested with a fake hub. This is the only place that registers hub event handlers. The
// React hook (useSignalR) only creates the connection and calls connectChat.

// The part of a SignalR HubConnection this code uses. Declared here so a test can supply a fake.
export interface ChatHub {
  on: (method: string, handler: (...args: never[]) => void) => void
  onreconnecting: (callback: () => void) => void
  onreconnected: (callback: () => void) => void
  onclose: (callback: () => void) => void
  start: () => Promise<void>
  stop: () => Promise<void>
  invoke: (method: string, ...args: unknown[]) => Promise<unknown>
}

export interface ChatConnectionDeps {
  // The signed-in user, so the typing events caused by their own other tabs can be ignored.
  getUserId: () => string | null
  // Called after the connection came back from a drop and is ready. Events sent during the gap
  // were lost, so this is where REST data is reloaded.
  onReconnected: () => void
  // Called with every presence change pushed by the server (someone came online or went offline).
  onPresenceChanged: (presence: Presence) => void
  retryDelay?: (failedAttempts: number) => number
}

export interface ChatConnection {
  sendTyping: (conversationId: string, isTyping: boolean) => void
  dispose: () => void
}

// How long someone stays shown as typing without hearing from them again. Above the sender's
// refresh interval (2.5 seconds), so a steady typist never flickers, and short enough that a lost
// "stopped" event (a closed tab, a dropped connection) does not leave the indicator on.
export const TYPING_TTL_MS = 6000

// Stopping typing in a conversation nobody is in is a harmless no-op on the server. Here it is used
// as a round trip. When start() resolves the server may still be putting the connection into its
// conversation groups, but it handles a connection's calls only after that is done, so an answer to
// this call proves the groups are joined.
const EMPTY_GUID = '00000000-0000-0000-0000-000000000000'
export const READINESS_PING = { method: 'StopTyping', args: [EMPTY_GUID] } as const

export function connectChat(hub: ChatHub, deps: ChatConnectionDeps): ChatConnection {
  const retryDelay = deps.retryDelay ?? reconnectDelay
  const typingTimers = new Map<string, ReturnType<typeof setTimeout>>()
  let retryTimer: ReturnType<typeof setTimeout> | undefined
  let wakeRetry: (() => void) | undefined
  let disposed = false
  // Bumped each time the connection state changes. A readiness check that finishes after the
  // connection dropped again must not report "connected".
  let epoch = 0

  const chat = () => useChatStore.getState()

  // ##### Handlers #####
  // Handlers use block bodies and return nothing. The SignalR JavaScript client treats a returned
  // value as the reply to the server and logs an error when none was asked for.

  hub.on('ReceiveMessage', (message: Message) => {
    chat().receiveMessage(message, deps.getUserId() ?? undefined)
  })

  // Someone read a conversation up to readAt. For the signed-in user this is another tab of theirs
  // (or this one, a moment after it asked), so their own unread count follows.
  hub.on('MessageRead', (conversationId: string, userId: string, readAt: string) => {
    chat().applyRead(conversationId, userId, readAt, deps.getUserId() ?? undefined)
  })

  hub.on('PresenceChanged', (presence: Presence) => {
    deps.onPresenceChanged(presence)
  })

  hub.on('AddedToConversation', (conversation: Conversation) => {
    chat().addConversation(conversation)

    // Nothing else puts this connection into the new conversation's group. A refused join is not
    // retried: the server puts every connection into all of its user's groups on the next connect.
    hub.invoke('JoinConversation', conversation.id).catch(() => {})
  })

  hub.on('UserTyping', (conversationId: string, userId: string, displayName: string, isTyping: boolean) => {
    if (userId === deps.getUserId()) return

    const key = `${conversationId}:${userId}`
    clearTimeout(typingTimers.get(key))
    typingTimers.delete(key)

    chat().setTyping(conversationId, { userId, displayName }, isTyping)

    if (isTyping) {
      typingTimers.set(
        key,
        setTimeout(() => {
          typingTimers.delete(key)
          chat().setTyping(conversationId, { userId, displayName }, false)
        }, TYPING_TTL_MS),
      )
    }
  })

  // ##### Connection events #####

  hub.onreconnecting(() => {
    epoch++
    chat().setConnection('reconnecting')
  })

  hub.onreconnected(() => {
    void becomeReady().then((ready) => {
      if (ready) deps.onReconnected()
    })
  })

  // The library reconnects by itself after a connection drops, but not after a close it counts as final.
  // The server ends a connection that way when its access token runs out. The access token factory
  // renews the session when the connection starts again, so starting again is the whole remedy. Closing
  // is also how dispose() ends the connection, and that is not a loss.
  hub.onclose(() => {
    epoch++
    if (disposed) return

    chat().setConnection('disconnected')
    void connect({ afterLoss: true })
  })

  async function becomeReady(): Promise<boolean> {
    const mine = ++epoch

    try {
      await hub.invoke(READINESS_PING.method, ...READINESS_PING.args)
    } catch {
      // If the call failed because the connection dropped, the reconnecting or close event
      // reports it. Treating the failure as "ready" cannot leave the app stuck.
    }

    if (disposed || mine !== epoch) return false

    chat().setConnection('connected')
    return true
  }

  // Connects, and keeps trying until it does. The library only retries a connection that already worked,
  // so the first connect, and a connect after the library let go of a closed one, are retried here.
  // After a loss, whatever the server pushed in the meantime was missed, so the owner is told once the
  // connection is ready, as it is after any reconnect.
  let connecting = false

  async function connect({ afterLoss }: { afterLoss: boolean }) {
    if (connecting) return
    connecting = true
    let holdingGuard = true

    try {
      for (let attempt = 0; !disposed; attempt++) {
        try {
          await hub.start()
        } catch {
          // Also what a start abandoned by dispose() ends in, which is not worth reporting.
          if (disposed) return

          chat().setConnection('disconnected')
          await pause(retryDelay(attempt))
          continue
        }

        // Started. The guard is only for the attempts: a close during the readiness check below must be
        // able to start the next connect.
        connecting = false
        holdingGuard = false

        const ready = await becomeReady()
        if (ready && afterLoss) deps.onReconnected()
        return
      }
    } finally {
      // Only if it is still this call's: another connect may have started during the readiness check.
      if (holdingGuard) connecting = false
    }
  }

  function pause(ms: number): Promise<void> {
    return new Promise((resolve) => {
      wakeRetry = resolve
      retryTimer = setTimeout(resolve, ms)
    })
  }

  void connect({ afterLoss: false })

  return {
    // Typing signals are best effort. When the connection is down, or the server refuses, nothing
    // is lost that matters, so there is no queue and no error.
    sendTyping(conversationId, isTyping) {
      if (disposed || chat().connection !== 'connected') return

      hub.invoke(isTyping ? 'StartTyping' : 'StopTyping', conversationId).catch(() => {})
    },

    dispose() {
      if (disposed) return
      disposed = true

      typingTimers.forEach((timer) => clearTimeout(timer))
      typingTimers.clear()
      clearTimeout(retryTimer)
      wakeRetry?.()

      hub.stop().catch(() => {})
    },
  }
}
