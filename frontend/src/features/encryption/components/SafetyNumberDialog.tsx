import { useEffect, useState } from 'react'
import { ShieldCheck, TriangleAlert } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { acceptIdentity, verifyPerson, viewSafetyNumber } from '../safetyController'
import type { SafetyView } from '../safetyNumbers'
import { Problem } from './Problem'
import { useStep } from './useStep'
import { describeFailure } from '../messages'

interface SafetyNumberDialogProps {
  contactId: string
  contactName: string
  open: boolean
  onClose: () => void
}

// The number two people compare to be sure nobody is between them, and where a person is marked as verified or a changed key is
// accepted. The cryptography it needs is loaded with it, not with the page.
export function SafetyNumberDialog({ contactId, contactName, open, onClose }: SafetyNumberDialogProps) {
  return (
    <Dialog open={open} onClose={onClose} title={`Safety number with ${contactName}`}>
      <Comparing contactId={contactId} contactName={contactName} onDone={onClose} />
    </Dialog>
  )
}

// Rendered only while the dialog is open, so every opening shows the number as it is now.
function Comparing({ contactId, contactName, onDone }: { contactId: string; contactName: string; onDone: () => void }) {
  const [view, setView] = useState<SafetyView | null>(null)
  const [loadProblem, setLoadProblem] = useState<string | null>(null)
  // Counts the times the number was asked for again, after a mark or an acceptance.
  const [asked, setAsked] = useState(0)
  const { busy, problem, run } = useStep()

  useEffect(() => {
    let current = true
    viewSafetyNumber(contactId).then(
      (loaded) => {
        if (current) {
          setView(loaded)
          setLoadProblem(null)
        }
      },
      (failure: unknown) => {
        if (current) setLoadProblem(describeFailure(failure))
      },
    )
    return () => {
      current = false
    }
  }, [contactId, asked])

  return (
    <div className="space-y-4 overflow-y-auto p-5">
      <Problem message={loadProblem ?? problem} />
      {view === null && loadProblem === null && <p className="text-sm text-slate-500">Working out the number…</p>}

      {view?.changedTo != null && (
        <div role="alert" className="flex items-start gap-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
          <TriangleAlert className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
          <p>
            {contactName} has a different security key from the one this browser trusted before. That happens when they reset their
            encryption. It could also mean someone is between you. Compare the number below with {contactName} before you accept it.
          </p>
        </div>
      )}

      {view && (
        <>
          <ul aria-label="Safety number" className="grid grid-cols-3 gap-x-4 gap-y-2 rounded-lg border border-line bg-slate-50 px-4 py-3 font-mono text-base text-slate-900 sm:grid-cols-4">
            {view.number.split(' ').map((group, index) => (
              <li key={index} className="tracking-wider">
                {group}
              </li>
            ))}
          </ul>

          <p className="text-sm text-slate-600">
            Ask {contactName} to open this screen and read their number to you, or show it to you. If every group matches, you are talking
            to each other and nobody is between you. Use a call, or meet, so the number does not travel through this app.
          </p>

          {view.changedTo === null && view.verifiedAt !== null && (
            <p className="flex items-center gap-2 text-sm text-blue-700">
              <ShieldCheck className="h-4 w-4" aria-hidden="true" />
              You marked {contactName} as verified on {new Date(view.verifiedAt).toLocaleDateString()}.
            </p>
          )}

          <div className="flex flex-wrap justify-end gap-2">
            {view.changedTo !== null ? (
              <Button
                isLoading={busy}
                onClick={() =>
                  void run(() => acceptIdentity(contactId, view.changedTo!)).then((worked) => {
                    if (worked) onDone()
                  })
                }
              >
                Accept the new key
              </Button>
            ) : (
              <Button
                variant={view.verifiedAt === null ? 'primary' : 'ghost'}
                isLoading={busy}
                onClick={() =>
                  void run(() => verifyPerson(contactId, view.verifiedAt === null)).then((worked) => {
                    if (worked) setAsked((count) => count + 1)
                  })
                }
              >
                {view.verifiedAt === null ? 'The numbers match: mark as verified' : 'Remove the verified mark'}
              </Button>
            )}
            <Button variant="ghost" onClick={onDone}>
              Close
            </Button>
          </div>
        </>
      )}
    </div>
  )
}
