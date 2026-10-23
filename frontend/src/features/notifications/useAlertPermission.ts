import { useCallback, useState } from 'react'
import { type AlertPermission, alertPermission, requestAlertPermission } from './browserAlert'

// Whether the browser lets this site show alerts, and a way to ask. The request has to come from a click, so the
// caller wires `request` to a button.
export function useAlertPermission() {
  const [permission, setPermission] = useState<AlertPermission>(alertPermission)

  const request = useCallback(async () => {
    setPermission(await requestAlertPermission())
  }, [])

  return { permission, request }
}
