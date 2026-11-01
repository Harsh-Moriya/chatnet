import { Suspense, lazy, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { ArrowLeft, Bell, BellOff, Users } from 'lucide-react'
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

// Opened by a click and not needed to show a conversation, so it is fetched when it is first wanted and not with the page.
const GroupInfoDialog = lazy(() => import('./GroupInfoDialog').then((module) => ({ default: module.GroupInfoDialog })))

export function ChatHeader({ conversation }: { conversation: Conversation }) {
  const navigate = useNavigate()
  const { user } = useAuth()
  const now = useNow()
  const typing = typingLabel(useChatStore((state) => state.typing[conversation.id]))
  const isGroup = conversation.type === 'Group'
  // A group the reader left is only read: no group info, no muting, and no presence of people they cannot see.
  const left = conversation.leftAt !== null
  const [infoOpen, setInfoOpen] = useState(false)
  // Mounted from the first click on, so closing it returns focus to the button as the browser's dialog does.
  const [infoMounted, setInfoMounted] = useState(false)

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
          {muteError ?? (left ? 'You left this group' : (typing ?? presence ?? (isGroup ? 'Group conversation' : 'Direct message')))}
        </p>
      </div>

      {!left && (
        <div className="ml-auto flex items-center gap-1">
          {isGroup && (
            <Button
              variant="ghost"
              size="icon"
              aria-label="Group info"
              title="Who is in this group, and what you can do about it"
              onClick={() => {
                setInfoMounted(true)
                setInfoOpen(true)
              }}
            >
              <Users className="h-5 w-5" />
            </Button>
          )}

          <Button
            variant="ghost"
            size="icon"
            aria-label={conversation.isMuted ? 'Unmute conversation' : 'Mute conversation'}
            aria-pressed={conversation.isMuted}
            title={conversation.isMuted ? 'Muted: no alerts for new messages. Mentions still alert.' : 'Mute alerts for new messages'}
            disabled={changingMute}
            onClick={() => void toggleMute()}
          >
            {conversation.isMuted ? <BellOff className="h-5 w-5" /> : <Bell className="h-5 w-5" />}
          </Button>
        </div>
      )}

      {isGroup && !left && infoMounted && (
        <Suspense fallback={null}>
          <GroupInfoDialog conversation={conversation} open={infoOpen} onClose={() => setInfoOpen(false)} />
        </Suspense>
      )}
    </header>
  )
}
