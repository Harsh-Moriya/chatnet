import { useRef } from 'react'
import { ImagePlus, Trash2 } from 'lucide-react'
import { Avatar } from './Avatar'
import { Button } from './Button'

interface AvatarPickerProps {
  name: string
  src: string | null
  isGroup?: boolean
  // True while a change is being made: nothing else can be started.
  busy: boolean
  // What the person picked in the file chooser. The owner prepares it and uploads it.
  onPick: (file: File) => void
  onRemove: () => void
}

// The types the server accepts; the chooser only offers files of these kinds.
const ACCEPTED = 'image/png,image/jpeg,image/webp,image/gif'

// A picture with the buttons to change it. It knows nothing about where the picture goes.
export function AvatarPicker({ name, src, isGroup = false, busy, onPick, onRemove }: AvatarPickerProps) {
  const input = useRef<HTMLInputElement>(null)

  return (
    <div className="flex items-center gap-4">
      <Avatar name={name} src={src} size="lg" isGroup={isGroup} />

      <div className="flex flex-wrap gap-2">
        <input
          ref={input}
          type="file"
          accept={ACCEPTED}
          className="sr-only"
          tabIndex={-1}
          aria-label="Choose a picture"
          onChange={(event) => {
            const file = event.target.files?.[0]
            // Cleared so choosing the same file again, after a failure, still counts as a choice.
            event.target.value = ''
            if (file) onPick(file)
          }}
        />

        <Button variant="ghost" disabled={busy} onClick={() => input.current?.click()}>
          <ImagePlus className="h-4 w-4" />
          {src ? 'Change picture' : 'Add a picture'}
        </Button>

        {src && (
          <Button variant="ghost" disabled={busy} onClick={onRemove}>
            <Trash2 className="h-4 w-4" />
            Remove
          </Button>
        )}
      </div>
    </div>
  )
}
