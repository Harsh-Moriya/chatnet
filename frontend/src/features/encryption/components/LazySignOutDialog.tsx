import { Suspense, lazy } from 'react'

// Only asked for when signing out would delete encryption keys, so the dialog is fetched when it is first wanted and not with the page.
const SignOutDialog = lazy(() => import('./SignOutDialog').then((module) => ({ default: module.SignOutDialog })))

interface LazySignOutDialogProps {
  open: boolean
  onClose: () => void
}

// Mounted from the first time it is opened, so closing it returns focus to the button as the browser's dialog does.
export function LazySignOutDialog(props: LazySignOutDialogProps) {
  return (
    <Suspense fallback={null}>
      <SignOutDialog {...props} />
    </Suspense>
  )
}
