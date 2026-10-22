import {
  type ChangeEvent,
  type ClipboardEvent,
  type KeyboardEvent,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
} from 'react'
import { AlertCircle, Paperclip, Send } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { Textarea } from '../../../components/ui/Textarea'
import { cn } from '../../../lib/cn'
import { sendMessage } from '../api/chatApi'
import { FILE_PICKER_ACCEPT, MAX_FILES_PER_MESSAGE } from '../attachments'
import { useAttachmentUploads } from '../hooks/useAttachmentUploads'
import { useTypingIndicator } from '../hooks/useTypingIndicator'
import { canSend, toPendingAttachments } from '../uploads'
import { UploadChips } from './UploadChips'

// The server's limit, counted the way it counts: in UTF-16 code units, which is also what the
// browser's maxLength counts.
const MAX_LENGTH = 4000
const SHOW_COUNTER_FROM = 3500
const MAX_HEIGHT_PX = 160

export function MessageComposer({ conversationId }: { conversationId: string }) {
  const [text, setText] = useState('')
  const box = useRef<HTMLTextAreaElement>(null)
  const picker = useRef<HTMLInputElement>(null)
  const typing = useTypingIndicator(conversationId)
  const uploads = useAttachmentUploads(conversationId)

  const sendable = canSend(uploads.items, text)

  // Opening a conversation puts the cursor in the box on a desktop screen. On a phone that would
  // pop the keyboard up over the messages, so it is skipped there.
  useEffect(() => {
    if (window.matchMedia('(min-width: 768px)').matches) box.current?.focus()
  }, [])

  // Grows the box with its text, up to a limit, then scrolls inside. scrollHeight does not include
  // the border, and the box is border-sized, so the border is added back; without it the box would
  // always be 2px too short and show a scrollbar.
  useLayoutEffect(() => {
    const element = box.current
    if (!element) return

    element.style.height = 'auto'
    const wanted = element.scrollHeight + (element.offsetHeight - element.clientHeight)
    element.style.height = `${Math.min(wanted, MAX_HEIGHT_PX)}px`
    element.style.overflowY = wanted > MAX_HEIGHT_PX ? 'auto' : 'hidden'
  }, [text])

  function submit() {
    if (!sendable) return

    void sendMessage(conversationId, text, toPendingAttachments(uploads.items))
    typing.stop()
    setText('')
    uploads.clear()
  }

  function handleChange(event: ChangeEvent<HTMLTextAreaElement>) {
    setText(event.target.value)
    typing.input(event.target.value)
  }

  // Enter sends and Shift+Enter starts a new line. While an input method (Japanese, Chinese,
  // Korean) is composing, Enter confirms the composition and must not send.
  function handleKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) {
      event.preventDefault()
      submit()
    }
  }

  // A screenshot or a copied file pasted into the box is attached. Pasted text has no files in it and is
  // left to the box.
  function handlePaste(event: ClipboardEvent<HTMLTextAreaElement>) {
    const files = Array.from(event.clipboardData.files)
    if (files.length === 0) return

    event.preventDefault()
    uploads.add(files)
  }

  function handlePicked(event: ChangeEvent<HTMLInputElement>) {
    uploads.add(Array.from(event.target.files ?? []))

    // Cleared so that choosing the same file again (after removing it) is a change the browser reports.
    event.target.value = ''
  }

  return (
    <div className="shrink-0 border-t border-slate-200 bg-white p-3">
      {uploads.rejected.length > 0 && (
        <ul className="mb-2 space-y-0.5 text-xs text-red-500" role="alert">
          {uploads.rejected.map((problem) => (
            <li key={problem} className="flex items-start gap-1.5">
              <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
              <span>{problem}</span>
            </li>
          ))}
        </ul>
      )}

      {uploads.items.length > 0 && <UploadChips items={uploads.items} onRemove={uploads.remove} />}

      <div className="flex items-end gap-2">
        <input
          ref={picker}
          type="file"
          multiple
          hidden
          accept={FILE_PICKER_ACCEPT}
          aria-label="Choose files to attach"
          onChange={handlePicked}
        />
        <Button
          variant="ghost"
          size="icon"
          aria-label="Attach files"
          title={`Attach files (up to ${MAX_FILES_PER_MESSAGE})`}
          disabled={uploads.items.length >= MAX_FILES_PER_MESSAGE}
          onClick={() => picker.current?.click()}
          className="mb-0.5 h-10 w-10"
        >
          <Paperclip className="h-4 w-4" />
        </Button>

        <Textarea
          ref={box}
          rows={1}
          value={text}
          maxLength={MAX_LENGTH}
          placeholder="Write a message"
          aria-label="Message"
          onChange={handleChange}
          onKeyDown={handleKeyDown}
          onPaste={handlePaste}
          onBlur={typing.stop}
        />
        <Button
          size="icon"
          aria-label="Send message"
          disabled={!sendable}
          onClick={submit}
          className="mb-0.5 h-10 w-10"
        >
          <Send className="h-4 w-4" />
        </Button>
      </div>

      {text.length >= SHOW_COUNTER_FROM && (
        <p className={cn('mt-1 text-right text-xs', text.length >= MAX_LENGTH ? 'text-red-500' : 'text-slate-500')}>
          {text.length} / {MAX_LENGTH}
        </p>
      )}
    </div>
  )
}
