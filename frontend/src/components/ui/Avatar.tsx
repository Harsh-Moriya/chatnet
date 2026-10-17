import { Users } from 'lucide-react'
import { cn } from '../../lib/cn'

interface AvatarProps {
  name: string
  src?: string | null
  size?: 'sm' | 'md'
  // A group shows a people icon instead of initials.
  isGroup?: boolean
  // A green dot on the corner. The dot is a picture; the text for a screen reader is inside it.
  online?: boolean
}

// Colours stay inside the design tokens (blue, sky, slate). The same name always gets the same one,
// so a person keeps their colour everywhere.
const PALETTE = [
  'bg-blue-100 text-blue-700',
  'bg-sky-100 text-sky-700',
  'bg-slate-200 text-slate-700',
  'bg-blue-200 text-blue-800',
]

function initials(name: string): string {
  const words = name.trim().split(/\s+/).filter(Boolean)
  if (words.length === 0) return '?'

  const first = words[0][0]
  const last = words.length > 1 ? words[words.length - 1][0] : ''
  return (first + last).toUpperCase()
}

function colourFor(name: string): string {
  let hash = 0
  for (const char of name) hash = (hash * 31 + char.codePointAt(0)!) >>> 0
  return PALETTE[hash % PALETTE.length]
}

export function Avatar({ name, src, size = 'md', isGroup = false, online = false }: AvatarProps) {
  const box = size === 'md' ? 'h-10 w-10 text-sm' : 'h-8 w-8 text-xs'

  const face = src ? (
    <img src={src} alt="" className={cn('shrink-0 rounded-full object-cover', box)} />
  ) : (
    <div
      aria-hidden="true"
      className={cn(
        'flex shrink-0 select-none items-center justify-center rounded-full font-semibold',
        box,
        isGroup ? 'bg-slate-200 text-slate-600' : colourFor(name),
      )}
    >
      {isGroup ? <Users className={size === 'md' ? 'h-5 w-5' : 'h-4 w-4'} /> : initials(name)}
    </div>
  )

  if (!online) return face

  return (
    <span className="relative inline-flex shrink-0">
      {face}
      <span
        className={cn(
          'absolute bottom-0 right-0 rounded-full bg-emerald-500 ring-2 ring-white',
          size === 'md' ? 'h-3 w-3' : 'h-2.5 w-2.5',
        )}
      >
        <span className="sr-only">Online</span>
      </span>
    </span>
  )
}
