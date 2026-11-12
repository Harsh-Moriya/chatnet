// Runs the jobs of one key one after another, in the order they were queued, and the jobs of different keys side by side. A job that
// fails does not stop the ones behind it. For work that is asynchronous but must not overtake what came before it for the same thing,
// such as opening the messages that are pushed for a conversation.
export function createKeyedQueue() {
  const tails = new Map<string, Promise<void>>()

  return {
    run<T>(key: string, job: () => Promise<T>): Promise<T> {
      const result = (tails.get(key) ?? Promise.resolve()).then(job)
      const settled = result.then(
        () => undefined,
        () => undefined,
      )

      tails.set(key, settled)
      void settled.then(() => {
        if (tails.get(key) === settled) tails.delete(key)
      })

      return result
    },
  }
}
