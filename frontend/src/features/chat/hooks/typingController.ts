// Decides when to tell the server that the signed-in user is typing in one conversation, and when
// they stopped. Plain TypeScript with no React so the timing can be tested.
//
// The server shows "typing" to the others for as long as it keeps hearing about it, so while someone
// types a "start" is repeated every couple of seconds (the server also drops repeats that come
// faster than once a second). A pause, clearing the box or sending ends it with a "stop".

// How often to repeat "start" while typing continues. Below the receiver's timeout (6 seconds, see
// chatConnection.ts), so a refresh always arrives before the indicator would disappear.
export const TYPING_REFRESH_MS = 2500

// How long without a keystroke counts as having stopped.
export const TYPING_IDLE_MS = 3000

export interface TypingController {
  // Call with the current text on every change.
  input: (text: string) => void
  // Call when the message is sent or the box loses focus.
  stop: () => void
  // Stops, and cancels the pending timer. The controller can be used again afterwards.
  dispose: () => void
}

export function createTypingController(send: (isTyping: boolean) => void): TypingController {
  let typing = false
  let lastSentAt = 0
  let idleTimer: ReturnType<typeof setTimeout> | undefined

  function stop() {
    clearTimeout(idleTimer)
    idleTimer = undefined

    if (!typing) return

    typing = false
    send(false)
  }

  function input(text: string) {
    // A box of only spaces is not a message, so it is not typing.
    if (text.trim() === '') {
      stop()
      return
    }

    const now = Date.now()
    if (!typing || now - lastSentAt >= TYPING_REFRESH_MS) {
      typing = true
      lastSentAt = now
      send(true)
    }

    clearTimeout(idleTimer)
    idleTimer = setTimeout(stop, TYPING_IDLE_MS)
  }

  return { input, stop, dispose: stop }
}
