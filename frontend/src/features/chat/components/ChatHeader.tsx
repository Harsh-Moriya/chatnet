import { useNavigate } from 'react-router-dom'
import { ArrowLeft } from 'lucide-react'
import { Avatar } from '../../../components/ui/Avatar'
import { Button } from '../../../components/ui/Button'
import { useChatStore } from '../store/chatStore'
import { typingLabel } from '../typingLabel'
import type { Conversation } from '../types'

export function ChatHeader({ conversation }: { conversation: Conversation }) {
  const navigate = useNavigate()
  const typing = typingLabel(useChatStore((state) => state.typing[conversation.id]))
  const isGroup = conversation.type === 'Group'

  return (
    <header className="flex h-16 shrink-0 items-center gap-3 border-b border-slate-200 bg-white px-4">
      {/* On a phone the list and the conversation are separate screens; this goes back to the list. */}
      <Button
        variant="ghost"
        size="icon"
        aria-label="Back to conversations"
        className="md:hidden"
        onClick={() => navigate('/chat')}
      >
        <ArrowLeft className="h-5 w-5" />
      </Button>

      <Avatar name={conversation.name} src={conversation.avatarUrl} isGroup={isGroup} />

      <div className="min-w-0">
        <h1 className="truncate text-sm font-semibold text-slate-900">{conversation.name}</h1>
        <p className="truncate text-xs text-slate-500" aria-live="polite">
          {typing ?? (isGroup ? 'Group conversation' : 'Direct message')}
        </p>
      </div>
    </header>
  )
}
