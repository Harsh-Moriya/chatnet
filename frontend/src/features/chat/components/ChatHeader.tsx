import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { ArrowLeft, Bell, BellOff } from 'lucide-react'
import { Avatar } from '../../../components/ui/Avatar'
import { Button } from '../../../components/ui/Button'
import { cn } from '../../../lib/cn'
import { getApiError } from '../../../lib/errors'
import { useNow } from '../../../lib/useNow'
import { useAuth } from '../../auth/hooks/useAuth'
import { countOnline, directStatus, groupStatus } from '../../presence/presenceLabel'
import { usePresenceStore } from '../../presence/store/presenceStore'
import { setMuted } from '../api/chatApi'
import { useChatStore } from '../store/chatStore'
import { typingLabel } from '../typingLabel'
import type { Conversation } from '../types'

export function ChatHeader({ conversation }: { conversation: Conversation }) {
  const navigate = useNavigate()
  const { user } = useAuth()
  const now = useNow()
  const typing = typingLabel(useChatStore((state) => state.typing[conversation.id]))
  const isGroup = conversation.type === 'Group'

  // The people other than the signed-in user, for counting who is online in a group. Their presence is
  // read through a selector that returns a number, so the header re-renders when the count changes
  // and not on every presence change in the app.
  const members = useChatStore((state) => state.members[conversation.id])
  const others = useMemo(
    () => (members ?? []).filter((member) => member.userId !== user?.userId).map((member) => member.userId),
    [members, user?.userId],
  )
  const groupOnline = usePresenceStore((state) => (isGroup ? countOnline(others, state.byUser) : 0))
  const otherPresence = usePresenceStore((state) =>
    conversation.otherUserId === null ? undefined : state.byUser[conversation.otherUserId],
  )

  const presence = isGroup ? groupStatus(groupOnline) : directStatus(otherPresence, now)

  // The change is shown once the server has stored it, so the icon never claims a choice that was not kept.
  const [changingMute, setChangingMute] = useState(false)
  const [muteError, setMuteError] = useState<string | null>(null)

  async function toggleMute() {
    setChangingMute(true)
    try {
      await setMuted(conversation.id, !conversation.isMuted)
      setMuteError(null)
    } catch (error) {
      setMuteError(getApiError(error))
    } finally {
      setChangingMute(false)
    }
  }

  return (
    <header className="flex h-16 shrink-0 items-center gap-3 border-b border-line bg-white px-4">
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

      <Avatar
        name={conversation.name}
        src={conversation.avatarUrl}
        isGroup={isGroup}
        online={!isGroup && otherPresence?.isOnline === true}
      />

      <div className="min-w-0">
        <h1 className="truncate text-sm font-semibold text-slate-900">{conversation.name}</h1>
        {/* Typing wins over presence: it is what is happening now. Where nothing is known about
            presence yet, the plain description of the conversation stays. */}
        <p className={cn('truncate text-xs', muteError ? 'text-red-500' : 'text-slate-500')} aria-live="polite">
          {muteError ?? typing ?? presence ?? (isGroup ? 'Group conversation' : 'Direct message')}
        </p>
      </div>

      <Button
        variant="ghost"
        size="icon"
        className="ml-auto"
        aria-label={conversation.isMuted ? 'Unmute conversation' : 'Mute conversation'}
        aria-pressed={conversation.isMuted}
        title={conversation.isMuted ? 'Muted: no alerts for new messages. Mentions still alert.' : 'Mute alerts for new messages'}
        disabled={changingMute}
        onClick={() => void toggleMute()}
      >
        {conversation.isMuted ? <BellOff className="h-5 w-5" /> : <Bell className="h-5 w-5" />}
      </Button>
    </header>
  )
}
