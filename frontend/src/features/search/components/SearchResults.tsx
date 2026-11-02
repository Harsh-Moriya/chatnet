import type { ReactNode } from 'react'
import { AlertCircle, LoaderCircle } from 'lucide-react'
import { cn } from '../../../lib/cn'
import { Button } from '../../../components/ui/Button'
import { getApiError } from '../../../lib/errors'
import { useDebouncedValue } from '../../../lib/useDebouncedValue'
import { useNow } from '../../../lib/useNow'
import { useAuth } from '../../auth/hooks/useAuth'
import { useMessageSearch } from '../api/useMessageSearch'
import { MIN_TERM_LENGTH, searchTermFrom } from '../searchRules'
import { SearchHitRow } from './SearchHitRow'

const TYPING_PAUSE_MS = 300

// What the search box finds, in place of the list of conversations. The search starts when typing pauses, so a request is not
// sent for every keystroke; until then the results of the last search stay on screen, dimmed.
export function SearchResults({ input }: { input: string }) {
  const { user } = useAuth()
  const now = useNow()

  const term = searchTermFrom(useDebouncedValue(input, TYPING_PAUSE_MS))
  const search = useMessageSearch(term)

  const typed = searchTermFrom(input)
  if (typed === null) {
    return <Notice>Type at least {MIN_TERM_LENGTH} characters to search your messages.</Notice>
  }

  // The box has changed since the search that is showing was started.
  const outdated = typed !== term
  const hits = search.data?.pages.flat() ?? []

  if (search.isError && hits.length === 0) {
    return (
      <div className="flex flex-col items-center gap-3 p-6 text-center">
        <div className="flex items-start gap-2 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-600">
          <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" />
          <span>{getApiError(search.error)}</span>
        </div>
        <Button variant="ghost" size="sm" onClick={() => void search.refetch()}>
          Try again
        </Button>
      </div>
    )
  }

  if (hits.length === 0) {
    return search.isSuccess && !search.isFetching && !outdated ? (
      <Notice>No messages found for “{term}”.</Notice>
    ) : (
      <Notice>
        <span className="inline-flex items-center gap-2">
          <LoaderCircle className="h-4 w-4 animate-spin" aria-hidden="true" />
          Searching…
        </span>
      </Notice>
    )
  }

  return (
    <nav aria-label="Search results" className="flex-1 overflow-y-auto">
      <ul className={cn('divide-y divide-line-subtle transition-opacity', (outdated || search.isPlaceholderData) && 'opacity-60')}>
        {hits.map((hit) => (
          <SearchHitRow key={hit.messageId} hit={hit} currentUserId={user?.userId ?? ''} now={now} />
        ))}
      </ul>

      {search.hasNextPage && (
        <div className="flex flex-col items-center gap-2 border-t border-line-subtle p-3">
          {search.isFetchNextPageError && (
            <p role="alert" className="text-sm text-red-500">
              {getApiError(search.error)}
            </p>
          )}
          <Button variant="ghost" size="sm" isLoading={search.isFetchingNextPage} onClick={() => void search.fetchNextPage()}>
            {search.isFetchNextPageError ? 'Try again' : 'Show more results'}
          </Button>
        </div>
      )}
    </nav>
  )
}

// role="status" so a screen reader hears that the search is running or found nothing.
function Notice({ children }: { children: ReactNode }) {
  return (
    <p role="status" className="p-6 text-center text-sm text-slate-500">
      {children}
    </p>
  )
}
