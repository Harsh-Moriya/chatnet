import { useEffect, useRef, useState } from 'react'
import { Button } from '../../../components/ui/Button'
import { Textarea } from '../../../components/ui/Textarea'
import { getApiError } from '../../../lib/errors'
import { MAX_MESSAGE_LENGTH } from '../messageChanges'

interface MessageEditorProps {
  initial: string
  // A message of files may end up with no text; one without files may not.
  mayBeEmpty: boolean
  onSave: (content: string) => Promise<unknown>
  onCancel: () => void
}

// Editing a message where it is: the text in a box, Enter to save, Shift+Enter for a new line, Escape to give up.
// A refusal from the server (the window closed, the message was deleted) is shown here and the box stays, so the
// person keeps what they typed.
export function MessageEditor({ initial, mayBeEmpty, onSave, onCancel }: MessageEditorProps) {
  const [text, setText] = useState(initial)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const box = useRef<HTMLTextAreaElement>(null)

  useEffect(() => {
    const element = box.current
    if (!element) return

    element.focus()
    element.setSelectionRange(element.value.length, element.value.length)
  }, [])

  const trimmed = text.trim()
  const unchanged = trimmed === initial.trim()
  const canSave = !saving && !unchanged && (trimmed !== '' || mayBeEmpty)

  async function save() {
    if (unchanged) {
      onCancel()
      return
    }
    if (!canSave) return

    setSaving(true)
    setError(null)
    try {
      await onSave(trimmed)
      onCancel()
    } catch (failure) {
      setError(getApiError(failure))
      setSaving(false)
    }
  }

  return (
    <div className="w-full min-w-[min(16rem,70vw)]">
      <Textarea
        ref={box}
        rows={Math.min(8, Math.max(2, text.split('\n').length))}
        value={text}
        maxLength={MAX_MESSAGE_LENGTH}
        disabled={saving}
        aria-label="Edit message"
        error={error ?? undefined}
        className="text-slate-900"
        onChange={(event) => setText(event.target.value)}
        onKeyDown={(event) => {
          if (event.key === 'Escape') {
            event.preventDefault()
            onCancel()
          } else if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) {
            event.preventDefault()
            void save()
          }
        }}
      />
      <div className="mt-2 flex justify-end gap-2">
        <Button variant="ghost" size="sm" onClick={onCancel} disabled={saving}>
          Cancel
        </Button>
        <Button size="sm" onClick={() => void save()} disabled={!canSave} isLoading={saving}>
          Save
        </Button>
      </div>
    </div>
  )
}
