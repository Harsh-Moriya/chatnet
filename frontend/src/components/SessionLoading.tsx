import { Loader2 } from 'lucide-react'

// Shown from the page load until the server has said whether the browser still has a working session.
// It is brief (one request), but showing the sign-in form or redirecting before the answer would flash
// the wrong page at someone who is in fact signed in.
export function SessionLoading() {
  return (
    <div role="status" aria-live="polite" className="flex min-h-screen items-center justify-center bg-slate-50">
      <Loader2 className="h-6 w-6 animate-spin text-blue-600" aria-hidden="true" />
      <span className="sr-only">Loading your session</span>
    </div>
  )
}
