import { useEffect, useState } from 'react'

// Follows `value` but only after it has stopped changing for `delayMs`. Used so a search box does
// not send a request for every keystroke.
export function useDebouncedValue<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = useState(value)

  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), delayMs)
    return () => clearTimeout(timer)
  }, [value, delayMs])

  return debounced
}
