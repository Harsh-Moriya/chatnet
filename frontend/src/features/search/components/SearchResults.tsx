import type { ReactNode } from 'react'
import { AlertCircle, LoaderCircle } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { cn } from '../../../lib/cn'
import { useDebouncedValue } from '../../../lib/useDebouncedValue'
import { useNow } from '../../../lib/useNow'
import { useAuth } from '../../auth/hooks/useAuth'
import { useMessageSearch } from '../hooks/useMessageSearch'
import { MIN_TERM_LENGTH, searchTermFrom } from '../searchRules'
import { viewOf } from '../searchState'
import { useSyncStatus } from '../sync/status'
import { SearchHitRow } from './SearchHitRow'

// A search over what is on this device is quick, so it waits only as long as typing takes to pause.
const TYPING_PAUSE_MS = 150

// What the search box finds, in place of the list of conversations. Until typing pauses, the results of the last search stay on screen,
// dimmed.
export function SearchResults({ input }: { input: string }) {
  const { user } = useAuth()
  const now = useNow()

  const term = searchTermFrom(useDebouncedValue(input, TYPING_PAUSE_MS))
  const search = useMessageSearch(term)
  const unavailable = useSyncStatus((state) => state.unavailable)
  const historyPending = useSyncStatus((state) => state.historyPending)

  const typed = searchTermFrom(input)
  const hits = search.data?.pages.flatMap((page) => page.hits) ?? []
  const outdated = typed !== term

  const view = viewOf({
    typed,
    hasTerms: typed === null || search.hasTerms || outdated,
    unavailable,
    historyPending,
    hitCount: hits.length,
    failed: search.isError,
    settled: search.isSuccess && !search.isFetching && !outdated,
  })

  switch (view.kind) {
    case 'short':
      return <Notice>Type at least {MIN_TERM_LENGTH} characters to search your messages.</Notice>

    case 'nothing':
      return <Notice>Add a word to look for. Words with a minus before them are left out.</Notice>

    case 'unavailable':
      return <Notice>Search is not available in this browser, which cannot keep your messages on this device.</Notice>

    case 'searching':
      return (
        <Notice>
          <span className="inline-flex items-center gap-2">
            <LoaderCircle className="h-4 w-4 animate-spin" aria-hidden="true" />
            Searching…
          </span>
        </Notice>
      )

    case 'failed':
      return (
        <div className="flex flex-col items-center gap-3 p-6 text-center">
          <div className="flex items-start gap-2 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-600">
            <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" />
            <span>The messages on this device could not be searched.</span>
          </div>
          <Button variant="ghost" size="sm" onClick={() => void search.refetch()}>
            Try again
          </Button>
        </div>
      )

    case 'empty':
      return (
        <Notice>
          No messages found for “{term}”.
          {view.stillReading && <StillReading />}
        </Notice>
      )

    case 'hits':
      return (
        <nav aria-label="Search results" className="flex-1 overflow-y-auto">
          <ul className={cn('divide-y divide-line-subtle transition-opacity', (outdated || search.isPlaceholderData) && 'opacity-60')}>
            {hits.map((hit) => (
              <SearchHitRow key={`${hit.conversationId}/${hit.messageId}`} hit={hit} currentUserId={user?.userId ?? ''} now={now} />
            ))}
          </ul>

          {search.hasNextPage && (
            <div className="flex flex-col items-center gap-2 border-t border-line-subtle p-3">
              <Button variant="ghost" size="sm" isLoading={search.isFetchingNextPage} onClick={() => void search.fetchNextPage()}>
                Show more results
              </Button>
            </div>
          )}

          {view.stillReading && (
            <p role="status" className="px-4 pb-4 pt-1 text-center text-xs text-slate-500">
              <StillReading />
            </p>
          )}
        </nav>
      )
  }
}

// The search covers what this device has so far: what is older is still being read in.
function StillReading() {
  return <span className="mt-2 block text-xs">Older messages are still being downloaded to this device, so this may find more soon.</span>
}

// role="status" so a screen reader hears that the search is running or found nothing.
function Notice({ children }: { children: ReactNode }) {
  return (
    <p role="status" className="p-6 text-center text-sm text-slate-500">
      {children}
    </p>
  )
}
