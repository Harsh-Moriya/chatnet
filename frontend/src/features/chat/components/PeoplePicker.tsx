import { useState } from 'react'
import { Check, Search } from 'lucide-react'
import { Avatar } from '../../../components/ui/Avatar'
import { Input } from '../../../components/ui/Input'
import { cn } from '../../../lib/cn'
import { getApiError } from '../../../lib/errors'
import { useDebouncedValue } from '../../../lib/useDebouncedValue'
import { useUserSearch } from '../api/useUserSearch'
import type { UserSummary } from '../types'

interface PeoplePickerProps {
  onPick: (person: UserSummary) => void
  // Set when people are being collected: the ones chosen so far are ticked. Left out, picking one person is the whole action.
  selectedIds?: ReadonlySet<string>
  // People who are not offered, such as those who are in the group already.
  excludeIds?: ReadonlySet<string>
  disabled?: boolean
}

// A search box over the people of the app and the list of what it finds. Searching, and what is shown while it runs, is
// the same wherever people are chosen: starting a chat, making a group, adding to one.
export function PeoplePicker({ onPick, selectedIds, excludeIds, disabled = false }: PeoplePickerProps) {
  const [term, setTerm] = useState('')
  const search = useUserSearch(useDebouncedValue(term, 250))

  const enoughToSearch = term.trim().length >= 2
  const results = (search.data ?? []).filter((person) => !excludeIds?.has(person.id))

  return (
    <>
      <Input
        aria-label="Search people"
        placeholder="Search by name or username"
        leftIcon={<Search className="h-4 w-4" />}
        value={term}
        onChange={(event) => setTerm(event.target.value)}
      />

      <div className="min-h-24 flex-1 overflow-y-auto">
        {!enoughToSearch && <p className="py-4 text-center text-sm text-slate-500">Type at least 2 letters.</p>}

        {enoughToSearch && search.isError && (
          <p className="py-4 text-center text-sm text-red-500">{getApiError(search.error)}</p>
        )}

        {enoughToSearch && !search.isError && results.length === 0 && (
          <p className="py-4 text-center text-sm text-slate-500">{search.isFetching ? 'Searching…' : 'No one found.'}</p>
        )}

        {enoughToSearch && results.length > 0 && (
          <ul className={cn('transition-opacity', search.isFetching && 'opacity-60')}>
            {results.map((person) => {
              const selected = selectedIds?.has(person.id) ?? false

              return (
                <li key={person.id}>
                  <button
                    type="button"
                    disabled={disabled}
                    onClick={() => onPick(person)}
                    aria-pressed={selectedIds ? selected : undefined}
                    className="flex w-full items-center gap-3 rounded-lg px-2 py-2 text-left transition-colors hover:bg-slate-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 disabled:opacity-50"
                  >
                    <Avatar name={person.displayName} src={person.avatarUrl} />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-sm font-medium text-slate-900">{person.displayName}</span>
                      <span className="block truncate text-xs text-slate-500">@{person.userName}</span>
                    </span>
                    {selected && <Check className="h-4 w-4 shrink-0 text-blue-600" />}
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
