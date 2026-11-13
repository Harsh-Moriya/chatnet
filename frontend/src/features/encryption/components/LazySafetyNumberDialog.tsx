import { Suspense, lazy } from 'react'

// Opened by a click and not needed to show a conversation: the dialog and the cryptography it uses are fetched when it is first wanted,
// and not with the page.
const SafetyNumberDialog = lazy(() => import('./SafetyNumberDialog').then((module) => ({ default: module.SafetyNumberDialog })))

interface LazySafetyNumberDialogProps {
  contactId: string
  contactName: string
  open: boolean
  onClose: () => void
}

// Mounted from the first time it is opened, so closing it returns focus to what opened it.
export function LazySafetyNumberDialog(props: LazySafetyNumberDialogProps) {
  return (
    <Suspense fallback={null}>
      <SafetyNumberDialog {...props} />
    </Suspense>
  )
}
