import { useChatStore } from '../store/chatStore'
import { typingLabel } from '../typingLabel'

// A small pill that floats just above the message bar while someone is typing, and takes no room when nobody is: the thread runs down to
// the bar, and the line the page reserved for it was a band of empty space. The header says the same in words, so this is the glance.
// The region is always there, so a screen reader hears the change.
export function TypingIndicator({ conversationId }: { conversationId: string }) {
  const label = typingLabel(useChatStore((state) => state.typing[conversationId]))

  return (
    <div aria-live="polite" className="pointer-events-none absolute bottom-full left-4 mb-2">
      {label && (
        <span className="inline-flex items-center gap-1.5 rounded-full bg-surface px-3 py-1 text-xs text-fg-muted shadow-card">
          <span className="flex gap-0.5" aria-hidden="true">
            {[0, 150, 300].map((delay) => (
              <span
                key={delay}
                className="h-1 w-1 animate-bounce rounded-full bg-fg-faint"
                style={{ animationDelay: `${delay}ms` }}
              />
            ))}
          </span>
          {label}
        </span>
      )}
    </div>
  )
}
