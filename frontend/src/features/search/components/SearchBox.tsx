import type { ReactNode } from 'react'
import { Search, X } from 'lucide-react'
import { Input } from '../../../components/ui/Input'
import { MAX_TERM_LENGTH } from '../searchRules'
import { Button } from '../../../components/ui/Button'

interface SearchBoxProps {
  value: string
  onChange: (value: string) => void
  // What the box is for, as its label and its hint: it searches the messages, or filters the conversations.
  label?: string
}

// The row that holds the search box at the top of every sidebar panel, so the box is in the same place in each of them.
export function SearchRow({ children }: { children: ReactNode }) {
  return (
    <div role="search" className="shrink-0 border-b border-line px-4 py-3">
      {children}
    </div>
  )
}

// The box at the top of a sidebar panel. Escape, or the cross, empties it.
export function SearchBox({ value, onChange, label = 'Search messages' }: SearchBoxProps) {
  return (
    <SearchRow>
      <Input
        aria-label={label}
        placeholder={label}
        autoComplete="off"
        enterKeyHint="search"
        maxLength={MAX_TERM_LENGTH}
        leftIcon={<Search className="h-4 w-4" />}
        rightElement={
          value !== '' ? (
            <Button type="button" variant="ghost" size="icon-sm" aria-label="Clear search" onClick={() => onChange('')}>
              <X className="h-4 w-4" />
            </Button>
          ) : undefined
        }
        value={value}
        onChange={(event) => onChange(event.target.value)}
        onKeyDown={(event) => {
          if (event.key === 'Escape') onChange('')
        }}
      />
    </SearchRow>
  )
}
