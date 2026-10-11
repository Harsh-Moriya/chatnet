import { type ChangeEvent, type KeyboardEvent, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { Send } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { Textarea } from '../../../components/ui/Textarea'
import { cn } from '../../../lib/cn'
import { sendMessage } from '../api/chatApi'
import { useTypingIndicator } from '../hooks/useTypingIndicator'

// The server's limit, counted the way it counts: in UTF-16 code units, which is also what the
// browser's maxLength counts.
const MAX_LENGTH = 4000
const SHOW_COUNTER_FROM = 3500
const MAX_HEIGHT_PX = 160

export function MessageComposer({ conversationId }: { conversationId: string }) {
  const [text, setText] = useState('')
  const box = useRef<HTMLTextAreaElement>(null)
  const typing = useTypingIndicator(conversationId)

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
    if (text.trim() === '') return

    void sendMessage(conversationId, text)
    typing.stop()
    setText('')
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

  return (
    <div className="shrink-0 border-t border-slate-200 bg-white p-3">
      <div className="flex items-end gap-2">
        <Textarea
          ref={box}
          rows={1}
          value={text}
          maxLength={MAX_LENGTH}
          placeholder="Write a message"
          aria-label="Message"
          onChange={handleChange}
          onKeyDown={handleKeyDown}
          onBlur={typing.stop}
        />
        <Button
          size="icon"
          aria-label="Send message"
          disabled={text.trim() === ''}
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
