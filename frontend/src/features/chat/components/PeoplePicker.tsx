import { useState } from 'react'
import { Check, Search } from 'lucide-react'
import { Avatar } from '../../../components/ui/Avatar'
import { Input } from '../../../components/ui/Input'
import { cn } from '../../../lib/cn'
import { getApiError } from '../../../lib/errors'
import { useDebouncedValue } from '../../../lib/useDebouncedValue'
import { SearchRow } from '../../search/components/SearchBox'
import { useUserSearch } from '../api/useUserSearch'
import { newPeopleFirst } from '../peopleOrder'
import type { UserSummary } from '../types'

const NO_ONE: ReadonlySet<string> = new Set()

interface PeoplePickerProps {
  onPick: (person: UserSummary) => void
  // Set when people are being collected: the ones chosen so far are ticked. Left out, picking one person is the whole action.
  selectedIds?: ReadonlySet<string>
  // People who are not offered, such as those who are in the group already.
  excludeIds?: ReadonlySet<string>
  disabled?: boolean
  // The people already chatted with: they are tagged, and listed after the people there is no conversation with yet.
  contactIds?: ReadonlySet<string>
  // What is in the box when it first appears, for a search carried over from somewhere else.
  initialTerm?: string
  // Set in a sidebar panel: the box sits in the row every panel has at its top, and the list is inset to match. Left out (a dialog), the
  // box and the list are plain.
  inPanel?: boolean
}

// A search box over the people of the app and the list of what it finds. Searching, and what is shown while it runs, is
// the same wherever people are chosen: starting a chat, making a group, adding to one.
export function PeoplePicker({ onPick, selectedIds, excludeIds, disabled = false, contactIds, initialTerm = '', inPanel = false }: PeoplePickerProps) {
  const [term, setTerm] = useState(initialTerm)
  const search = useUserSearch(useDebouncedValue(term, 250))

  const enoughToSearch = term.trim().length >= 2
  const results = newPeopleFirst((search.data ?? []).filter((person) => !excludeIds?.has(person.id)), contactIds ?? NO_ONE)

  const box = (
    <Input
      aria-label="Search people"
      placeholder="Search by name or username"
      leftIcon={<Search className="h-4 w-4" />}
      value={term}
      onChange={(event) => setTerm(event.target.value)}
    />
  )

  return (
    <>
      {inPanel ? <SearchRow>{box}</SearchRow> : box}

      <div className={cn('min-h-24 flex-1 overflow-y-auto', inPanel && 'px-4')}>
        {!enoughToSearch && <p className="py-4 text-center text-sm text-fg-muted">Type at least 2 letters.</p>}

        {enoughToSearch && search.isError && (
          <p className="py-4 text-center text-sm text-danger-fg">{getApiError(search.error)}</p>
        )}

        {enoughToSearch && !search.isError && results.length === 0 && (
          <p className="py-4 text-center text-sm text-fg-muted">{search.isFetching ? 'Searching…' : 'No one found.'}</p>
        )}

        {enoughToSearch && results.length > 0 && (
          <ul className={cn('transition-opacity', search.isFetching && 'opacity-60')}>
            {results.map((person) => {
              const selected = selectedIds?.has(person.id) ?? false
              const chatted = contactIds?.has(person.id) ?? false

              return (
                <li key={person.id}>
                  <button
                    type="button"
                    disabled={disabled}
                    onClick={() => onPick(person)}
                    aria-pressed={selectedIds ? selected : undefined}
                    className={cn(
                      'flex w-full items-center gap-3 rounded-lg px-2 py-2 text-left transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 disabled:opacity-50',
                      selected ? 'bg-accent-soft' : 'hover:bg-hover',
                    )}
                  >
                    <Avatar name={person.displayName} src={person.avatarUrl} />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-sm font-medium text-fg">{person.displayName}</span>
                      <span className="block truncate text-xs text-fg-muted">@{person.userName}</span>
                    </span>
                    {chatted && <span className="shrink-0 rounded-full bg-subtle px-2 py-0.5 text-[11px] text-fg-muted">In your chats</span>}
                    {selectedIds && (
                      <span
                        aria-hidden="true"
                        className={cn(
                          'flex h-5 w-5 shrink-0 items-center justify-center rounded-md border',
                          selected ? 'border-blue-600 bg-blue-600 text-white' : 'border-line',
                        )}
                      >
                        {selected && <Check className="h-3.5 w-3.5" />}
                      </span>
                    )}
                  </button>
                </li>
              )
            })}
          </ul>
        )}
      </div>
    </>
  )
}
