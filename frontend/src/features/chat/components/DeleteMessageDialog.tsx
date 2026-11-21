import { useState } from 'react'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { getApiError } from '../../../lib/errors'
import { Alert } from '../../../components/ui/Alert'

interface DeleteMessageDialogProps {
  open: boolean
  // True when the reader deletes someone else's message as an admin: the words say whose it is.
  someoneElses: boolean
  onConfirm: () => Promise<unknown>
  onClose: () => void
}

// Asks before deleting, because it cannot be undone: the text and the files are removed for everyone in the
// conversation. The dialog stays open with the reason when the server refuses.
export function DeleteMessageDialog({ open, someoneElses, onConfirm, onClose }: DeleteMessageDialogProps) {
  const [deleting, setDeleting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  function close() {
    setError(null)
    onClose()
  }

  async function confirm() {
    setDeleting(true)
    setError(null)
    try {
      await onConfirm()
      close()
    } catch (failure) {
      setError(getApiError(failure))
    } finally {
      setDeleting(false)
    }
  }

  return (
    <Dialog open={open} onClose={close} title="Delete message">
      <div className="space-y-4 px-5 py-4">
        <p className="text-sm text-fg-soft">
          {someoneElses
            ? 'This message disappears for everyone in the conversation. The person who sent it is not asked.'
            : 'This message disappears for everyone in the conversation.'}{' '}
          Its files are deleted too. This cannot be undone.
        </p>

        {error && (
          <Alert role="alert">{error}</Alert>
        )}

        <div className="flex justify-end gap-2">
          <Button variant="secondary" onClick={close} disabled={deleting}>
            Cancel
          </Button>
          <Button onClick={() => void confirm()} isLoading={deleting}>
            Delete
          </Button>
        </div>
      </div>
    </Dialog>
  )
}
