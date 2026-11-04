import { useCallback, useEffect, useState } from 'react'
import { useAuthStore } from '../../auth/store/authStore'
import { pushErrorMessage } from '../pushErrors'
import { readFacts, turnOffPush, turnOnPush } from '../pushController'
import { type PushFacts, type SwitchState, switchState } from '../pushPlan'
import { usePushKey } from './usePushKey'

// The switch in the profile: what it shows, and what turning it on or off does. "loading" until the server has said whether it has
// push keys and the browser has said what it holds.
export function usePushSwitch() {
  const userId = useAuthStore((state) => state.user?.userId)
  const key = usePushKey(userId !== undefined)
  const publicKey = key.data ?? null

  const [facts, setFacts] = useState<PushFacts | null>(null)
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  // What is true now, read again after the person changes it.
  const reload = useCallback(async () => {
    if (userId === undefined) return
    setFacts(await readFacts(userId, publicKey))
  }, [userId, publicKey])

  // Read once the server has said whether it has push keys, and again if that answer or the person changes.
  useEffect(() => {
    if (!key.isSuccess || userId === undefined) return

    let cancelled = false
    readFacts(userId, publicKey).then(
      (read) => {
        if (!cancelled) setFacts(read)
      },
      () => {
        if (!cancelled) setProblem('Could not read the state of alerts in this browser.')
      },
    )

    return () => {
      cancelled = true
    }
  }, [key.isSuccess, userId, publicKey])

  async function run(action: () => Promise<string | null>) {
    setBusy(true)
    try {
      setProblem(await action())
      await reload()
    } catch (failure) {
      setProblem(pushErrorMessage(failure))
    } finally {
      setBusy(false)
    }
  }

  // Called from the click, so the browser's permission prompt is allowed to show.
  const turnOn = () =>
    run(async () => {
      if (userId === undefined || publicKey === null) return null

      switch (await turnOnPush(userId, publicKey)) {
        case 'blocked':
          return 'Alerts are blocked for this site. Allow notifications in your browser settings, then try again.'
        case 'unsupported':
          return 'This browser cannot show alerts.'
        default:
          return null
      }
    })

  const turnOff = () =>
    run(async () => {
      if (userId !== undefined) await turnOffPush(userId)
      return null
    })

  const state: SwitchState | 'loading' = facts === null ? 'loading' : switchState(facts)

  return { state, busy, problem, turnOn, turnOff }
}
