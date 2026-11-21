import { Suspense, lazy, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { ArrowLeft, Bell, BellOff, Fingerprint, Images, Phone, ShieldCheck, Users, Video } from 'lucide-react'
import { Avatar } from '../../../components/ui/Avatar'
import { Button } from '../../../components/ui/Button'
import { cn } from '../../../lib/cn'
import { getApiError } from '../../../lib/errors'
import { useNow } from '../../../lib/useNow'
import { useStartCall } from '../../calls/useStartCall'
import { useAuth } from '../../auth/hooks/useAuth'
import { LazySafetyNumberDialog } from '../../encryption/components/LazySafetyNumberDialog'
import { useVerified } from '../../encryption/hooks/useVerified'
import { countOnline, directStatus, groupStatus } from '../../presence/presenceLabel'
import { usePresenceStore } from '../../presence/store/presenceStore'
import { setMuted } from '../api/chatApi'
import { useChatStore } from '../store/chatStore'
import { typingLabel } from '../typingLabel'
import type { Conversation } from '../types'

// Opened by a click and not needed to show a conversation, so it is fetched when it is first wanted and not with the page.
const GroupInfoDialog = lazy(() => import('./GroupInfoDialog').then((module) => ({ default: module.GroupInfoDialog })))
const GalleryDialog = lazy(() => import('../gallery/GalleryDialog').then((module) => ({ default: module.GalleryDialog })))

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
  // The number two people compare to be sure nobody is between them, for a direct chat. Mounted from the first click on, as the group info is.
  const [safetyOpen, setSafetyOpen] = useState(false)
  const [safetyMounted, setSafetyMounted] = useState(false)
  // The pictures, videos and PDFs of the conversation, mounted from the first click on as the others are.
  const [galleryOpen, setGalleryOpen] = useState(false)
  const [galleryMounted, setGalleryMounted] = useState(false)
  const verified = useVerified(isGroup ? null : conversation.otherUserId)
  const { canCall, call } = useStartCall(conversation)

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
    <header className="flex h-16 shrink-0 items-center gap-3 border-b border-line bg-surface px-4">
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
        <h1 className="truncate text-sm font-semibold text-fg">{conversation.name}</h1>
        {/* Typing wins over presence: it is what is happening now. Where nothing is known about
            presence yet, the plain description of the conversation stays. */}
        <p className={cn('truncate text-xs', muteError ? 'text-danger-fg' : 'text-fg-muted')} aria-live="polite">
          {muteError ?? (left ? 'You left this group' : (typing ?? presence ?? (isGroup ? 'Group conversation' : 'Direct message')))}
        </p>
      </div>

      <div className="ml-auto flex items-center gap-1">
        {/* Outside the guard for a group that was left: its history is still read, so its files are still looked at. */}
        <Button
          variant="ghost"
          size="icon"
          aria-label="Pictures, videos and PDFs"
          title="Every picture, video and PDF in this conversation"
          onClick={() => {
            setGalleryMounted(true)
            setGalleryOpen(true)
          }}
        >
          <Images className="h-5 w-5" />
        </Button>

        {!left && (
          <>
            {!isGroup && conversation.otherUserId !== null && (
              <>
                <Button
                  variant="ghost"
                  size="icon"
                  aria-label="Audio call"
                  title="Call by voice"
                  disabled={!canCall}
                  onClick={() => call('Audio')}
                >
                  <Phone className="h-5 w-5" />
                </Button>
                <Button
                  variant="ghost"
                  size="icon"
                  aria-label="Video call"
                  title="Call with video"
                  disabled={!canCall}
                  onClick={() => call('Video')}
                >
                  <Video className="h-5 w-5" />
                </Button>
              </>
            )}

            {!isGroup && conversation.otherUserId !== null && (
              <Button
                variant="ghost"
                size="icon"
                aria-label={verified ? 'Safety number, verified' : 'Safety number'}
                title={verified ? 'Verified: you compared the safety number' : 'Compare the safety number to be sure nobody is between you'}
                onClick={() => {
                  setSafetyMounted(true)
                  setSafetyOpen(true)
                }}
              >
                {verified ? <ShieldCheck className="h-5 w-5 text-accent-fg" /> : <Fingerprint className="h-5 w-5" />}
              </Button>
            )}

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
          </>
        )}
      </div>

      {galleryMounted && (
        <Suspense fallback={null}>
          <GalleryDialog conversationId={conversation.id} open={galleryOpen} onClose={() => setGalleryOpen(false)} />
        </Suspense>
      )}

      {!isGroup && !left && safetyMounted && conversation.otherUserId !== null && (
        <LazySafetyNumberDialog
          contactId={conversation.otherUserId}
          contactName={conversation.name}
          open={safetyOpen}
          onClose={() => setSafetyOpen(false)}
        />
      )}

      {isGroup && !left && infoMounted && (
        <Suspense fallback={null}>
          <GroupInfoDialog conversation={conversation} open={infoOpen} onClose={() => setInfoOpen(false)} />
        </Suspense>
      )}
    </header>
  )
}
