import { useEffect, useState } from 'react'

// The current time, refreshed on an interval, for labels such as "Today" and "10:05 AM" that
// have to roll over on their own. Calling new Date() while rendering would give a different value
// on every render; this gives one value per interval, so a list renders consistently.
export function useNow(intervalMs = 30_000): Date {
  const [now, setNow] = useState(() => new Date())

  useEffect(() => {
    const timer = setInterval(() => setNow(new Date()), intervalMs)
    return () => clearInterval(timer)
  }, [intervalMs])

  return now
}
