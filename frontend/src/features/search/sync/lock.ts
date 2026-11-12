// One tab reads for a person at a time. A second tab that finds the work being done skips it: what the first one stores is what both
// would, and a tab that is closed gives the work up, so the next run from any tab carries it on. Where the browser has no Web Locks,
// every tab goes ahead, which costs a repeat of the requests and nothing else, because what is stored is the same either way.
export async function withReadLock<T>(userId: string, work: () => Promise<T>): Promise<T | null> {
  const locks = typeof navigator === 'undefined' ? undefined : navigator.locks
  if (!locks) return work()

  return locks.request(`chatnet-local-messages:${userId}`, { ifAvailable: true }, async (lock) => (lock ? work() : null))
}
