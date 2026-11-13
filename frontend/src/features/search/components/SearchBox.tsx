import { Search, X } from 'lucide-react'
import { Input } from '../../../components/ui/Input'
import { MAX_TERM_LENGTH } from '../searchRules'

interface SearchBoxProps {
  value: string
  onChange: (value: string) => void
}

// The box above the list of conversations. Escape, or the cross, empties it, which brings the list back.
export function SearchBox({ value, onChange }: SearchBoxProps) {
  return (
    <div role="search" className="shrink-0 border-b border-line px-4 py-3">
      <Input
        aria-label="Search messages"
        placeholder="Search messages"
        autoComplete="off"
        enterKeyHint="search"
        maxLength={MAX_TERM_LENGTH}
        leftIcon={<Search className="h-4 w-4" />}
        rightElement={
          value !== '' ? (
            <button
              type="button"
              aria-label="Clear search"
              onClick={() => onChange('')}
              className="rounded text-slate-400 transition-colors hover:text-slate-600 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
            >
              <X className="h-4 w-4" />
            </button>
          ) : undefined
        }
        value={value}
        onChange={(event) => onChange(event.target.value)}
        onKeyDown={(event) => {
          if (event.key === 'Escape') onChange('')
        }}
      />
    </div>
  )
}
