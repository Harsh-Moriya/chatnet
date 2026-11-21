import { Suspense, lazy, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { ArrowLeft, Bell, BellOff, Fingerprint, Images, type LucideIcon, Phone, ShieldCheck, Users, Video } from 'lucide-react'
import { Avatar } from '../../../components/ui/Avatar'
import { Button } from '../../../components/ui/Button'
import { Menu } from '../../../components/ui/Menu'
import { Tooltip } from '../../../components/ui/Tooltip'
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
import { actionsInMenu } from '../headerActions'
import { useChatStore } from '../store/chatStore'
import { typingLabel } from '../typingLabel'
import type { Conversation } from '../types'

// Opened by a click and not needed to show a conversation, so it is fetched when it is first wanted and not with the page.
const GroupInfoDialog = lazy(() => import('./GroupInfoDialog').then((module) => ({ default: module.GroupInfoDialog })))
const GalleryDialog = lazy(() => import('../gallery/GalleryDialog').then((module) => ({ default: module.GalleryDialog })))

// One action of the header: a button, and on a narrow header (see `actionsInMenu`) an item of the three-dot menu as well.
interface HeaderAction {
  key: string
  // The button's name for a screen reader.
  label: string
  // What the tooltip says, which is more than the label.
  description: string
  // The words in the menu, where the label of a toggle would read oddly.
  menuLabel: string
  Icon: LucideIcon
  // In the accent colour, for a safety number that was compared.
  accent?: boolean
  onSelect: () => void
  disabled?: boolean
  pressed?: boolean
  // Always a button, whatever the width: a call, or the group's info.
  essential: boolean
}

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

  const direct = !isGroup && conversation.otherUserId !== null
  // In the order of the header. A group that was left keeps only its pictures: its history is still read, so its files are still looked at.
  const actions: HeaderAction[] = [
    {
      key: 'gallery',
      label: 'Pictures, videos and PDFs',
      description: 'Every picture, video and PDF in this conversation',
      menuLabel: 'Pictures, videos and PDFs',
      Icon: Images,
      onSelect: () => {
        setGalleryMounted(true)
        setGalleryOpen(true)
      },
      essential: false,
    },
    ...(left
      ? []
      : [
          ...(direct
            ? [
                { key: 'audio', label: 'Audio call', description: 'Call by voice', menuLabel: 'Audio call', Icon: Phone, onSelect: () => call('Audio'), disabled: !canCall, essential: true },
                { key: 'video', label: 'Video call', description: 'Call with video', menuLabel: 'Video call', Icon: Video, onSelect: () => call('Video'), disabled: !canCall, essential: true },
                {
                  key: 'safety',
                  label: verified ? 'Safety number, verified' : 'Safety number',
                  description: verified ? 'Verified: you compared the safety number' : 'Compare the safety number to be sure nobody is between you',
                  menuLabel: verified ? 'Safety number, verified' : 'Safety number',
                  Icon: verified ? ShieldCheck : Fingerprint,
                  accent: verified,
                  onSelect: () => {
                    setSafetyMounted(true)
                    setSafetyOpen(true)
                  },
                  essential: false,
                },
              ]
            : []),
          ...(isGroup
            ? [
                {
                  key: 'group',
                  label: 'Group info',
                  description: 'Who is in this group, and what you can do about it',
                  menuLabel: 'Group info',
                  Icon: Users,
                  onSelect: () => {
                    setInfoMounted(true)
                    setInfoOpen(true)
                  },
                  essential: true,
                },
              ]
            : []),
          {
            key: 'mute',
            label: conversation.isMuted ? 'Unmute conversation' : 'Mute conversation',
            description: conversation.isMuted ? 'Muted: no alerts for new messages. Mentions still alert.' : 'Mute alerts for new messages',
            menuLabel: conversation.isMuted ? 'Unmute alerts' : 'Mute alerts',
            Icon: conversation.isMuted ? BellOff : Bell,
            pressed: conversation.isMuted,
            disabled: changingMute,
            onSelect: () => void toggleMute(),
            essential: false,
          },
        ]),
  ]
  const inMenu = actionsInMenu(actions)
  const menuKeys = new Set(inMenu.map((action) => action.key))

  return (
    <header className="@container relative z-20 flex h-16 shrink-0 items-center gap-3 border-b border-line bg-surface px-4">
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
        {actions.map((action) => (
          // `contents` leaves the button where it was in the row; the ones that live in the menu on a narrow header are not shown there.
          // `@md` is 28rem of the header's content, a header 480 px wide with its padding, and it is the header that is measured, not the window:
          // with the sidebar open the header is much narrower than the window.
          <span key={action.key} className={menuKeys.has(action.key) ? 'hidden @md:contents' : 'contents'}>
            <Tooltip label={action.description}>
              <Button
                variant="ghost"
                size="icon"
                aria-label={action.label}
                aria-pressed={action.pressed}
                disabled={action.disabled}
                onClick={action.onSelect}
              >
                <action.Icon className={cn('h-5 w-5', action.accent && 'text-accent-fg')} />
              </Button>
            </Tooltip>
          </span>
        ))}

        {inMenu.length > 0 && (
          <div className="@md:hidden">
            <Tooltip label="More actions">
              <Menu
                variant="toolbar"
                label="More actions"
                items={inMenu.map((action) => ({
                  label: action.menuLabel,
                  icon: <action.Icon className={cn('h-4 w-4', action.accent && 'text-accent-fg')} />,
                  onSelect: action.onSelect,
                  disabled: action.disabled,
                }))}
              />
            </Tooltip>
          </div>
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
