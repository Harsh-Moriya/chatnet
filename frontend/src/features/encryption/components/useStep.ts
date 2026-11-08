import { useCallback, useState } from 'react'
import { describeFailure } from '../messages'

// What every step on these screens needs: it is busy while it runs, a failure becomes a message to show, and a success clears it.
// Returns whether the step worked, so the caller can move on.
export function useStep() {
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const run = useCallback(async (step: () => Promise<unknown>): Promise<boolean> => {
    setBusy(true)
    try {
      await step()
      setProblem(null)
      return true
    } catch (failure) {
      setProblem(describeFailure(failure))
      return false
    } finally {
      setBusy(false)
    }
  }, [])

  return { busy, problem, run, clear: () => setProblem(null) }
}
