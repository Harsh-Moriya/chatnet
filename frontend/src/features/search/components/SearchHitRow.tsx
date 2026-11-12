import { Paperclip } from 'lucide-react'
import { Link, useLocation } from 'react-router-dom'
import { Avatar } from '../../../components/ui/Avatar'
import { cn } from '../../../lib/cn'
import { formatListTime } from '../../../lib/format'
import { useChatStore } from '../../chat/store/chatStore'
import { conversationPath, focusState, focusedMessageId } from '../../notifications/navigation'
import type { SnippetPart } from '../query/snippet'
import type { LocalHit } from '../search'

interface SearchHitRowProps {
  hit: LocalHit
  currentUserId: string
  now: Date
}

// One message that matched. A click opens its conversation at that message; the message travels in the navigation state, as it does for a
// notification, so the conversation view finds it in what is held or loads the history around it.
export function SearchHitRow({ hit, currentUserId, now }: SearchHitRowProps) {
  const location = useLocation()
  const opened = focusedMessageId(location.state) === hit.messageId

  // What the conversation is called is as this person sees it (a direct chat is named after the other person), which the list already holds.
  const conversation = useChatStore((state) => state.conversations[hit.conversationId])
  const name = conversation?.name ?? 'Conversation'

  return (
    <li>
      <Link
        to={conversationPath(hit.conversationId)}
        state={focusState(hit.messageId)}
        aria-current={opened ? 'true' : undefined}
        className={cn(
          'flex items-center gap-3 px-4 py-3 transition-colors hover:bg-slate-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-blue-500',
          opened && 'bg-blue-50 hover:bg-blue-50',
        )}
      >
        <Avatar name={name} src={conversation?.avatarUrl ?? null} isGroup={conversation?.type === 'Group'} />

        <div className="min-w-0 flex-1">
          <div className="flex items-baseline justify-between gap-2">
            <p className="truncate text-sm font-semibold text-slate-900">{name}</p>
            <time className="shrink-0 text-xs text-slate-500" dateTime={hit.createdAt}>
              {formatListTime(hit.createdAt, now)}
            </time>
          </div>

          {/* Two lines, so the matching word is still in view when it is not near the start of a long message. */}
          <p className="line-clamp-2 break-words text-sm text-slate-500">
            <span>{hit.senderId === currentUserId ? 'You' : hit.senderDisplayName}: </span>
            {hit.fileName !== null ? (
              <span className="inline-flex max-w-full items-center gap-1 align-bottom">
                <Paperclip className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
                <span className="truncate">{hit.fileName}</span>
              </span>
            ) : (
              <Snippet parts={hit.snippet} />
            )}
          </p>
        </div>
      </Link>
    </li>
  )
}

// The text around the match with the matching words picked out. Drawn piece by piece, so a message that contains markup is shown as text.
function Snippet({ parts }: { parts: SnippetPart[] }) {
  return (
    <>
      {parts.map((part, index) =>
        part.match ? (
          <mark key={index} className="rounded bg-blue-100 px-0.5 font-medium text-slate-900">
            {part.text}
          </mark>
        ) : (
          <span key={index}>{part.text}</span>
        ),
      )}
    </>
  )
}
