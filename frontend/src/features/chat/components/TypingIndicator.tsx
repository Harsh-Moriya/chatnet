import { useChatStore } from '../store/chatStore'
import { typingLabel } from '../typingLabel'

// Always takes up its line, even when empty, so the message list does not jump when someone starts
// or stops typing.
export function TypingIndicator({ conversationId }: { conversationId: string }) {
  const label = typingLabel(useChatStore((state) => state.typing[conversationId]))

  return (
    <div className="h-6 shrink-0 px-4 text-xs text-slate-500" aria-live="polite">
      {label && (
        <span className="inline-flex items-center gap-1.5">
          <span className="flex gap-0.5" aria-hidden="true">
            {[0, 150, 300].map((delay) => (
              <span
                key={delay}
                className="h-1 w-1 animate-bounce rounded-full bg-slate-400"
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
