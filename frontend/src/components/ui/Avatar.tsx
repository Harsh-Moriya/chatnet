import { useState } from 'react'
import { Users } from 'lucide-react'
import { cn } from '../../lib/cn'
import { colourFor } from './avatarColour'

interface AvatarProps {
  name: string
  src?: string | null
  size?: 'sm' | 'md' | 'lg'
  // A group shows a people icon instead of initials.
  isGroup?: boolean
  // A green dot on the corner. The dot is a picture; the text for a screen reader is inside it.
  online?: boolean
}

function initials(name: string): string {
  const words = name.trim().split(/\s+/).filter(Boolean)
  if (words.length === 0) return '?'

  const first = words[0][0]
  const last = words.length > 1 ? words[words.length - 1][0] : ''
  return (first + last).toUpperCase()
}

const BOX = { sm: 'h-8 w-8 text-xs', md: 'h-10 w-10 text-sm', lg: 'h-20 w-20 text-2xl' }
const GROUP_ICON = { sm: 'h-4 w-4', md: 'h-5 w-5', lg: 'h-8 w-8' }

export function Avatar({ name, src, size = 'md', isGroup = false, online = false }: AvatarProps) {
  const box = BOX[size]

  // A picture that cannot be loaded (deleted since, or the network failed) gives way to the initials instead of a broken
  // image. It is tried again if the address changes.
  const [failedSrc, setFailedSrc] = useState<string | null>(null)

  const face = src && src !== failedSrc ? (
    <img src={src} alt="" onError={() => setFailedSrc(src)} className={cn('shrink-0 rounded-full object-cover', box)} />
  ) : (
    <div
      aria-hidden="true"
      className={cn(
        'flex shrink-0 select-none items-center justify-center rounded-full font-semibold',
        box,
        isGroup ? 'bg-strong text-fg-soft' : colourFor(name),
      )}
    >
      {isGroup ? <Users className={GROUP_ICON[size]} /> : initials(name)}
    </div>
  )

  if (!online) return face

  return (
    <span className="relative inline-flex shrink-0">
      {face}
      <span
        className={cn(
          'absolute bottom-0 right-0 rounded-full bg-emerald-500 ring-2 ring-surface',
          size === 'md' ? 'h-3 w-3' : 'h-2.5 w-2.5',
        )}
      >
        <span className="sr-only">Online</span>
      </span>
    </span>
  )
}
