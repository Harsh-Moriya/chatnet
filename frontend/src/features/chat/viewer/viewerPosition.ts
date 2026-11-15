// Where the viewer is in a list that may not be fully loaded: what its counter says, and when it should ask for more.

// How close to the end of what is loaded the viewer is when it asks for the next page, so that stepping on does not wait for it.
export const PREFETCH_DISTANCE = 5

// "3 of 12", or "3 of 50+" while the list goes on past what is loaded.
export function positionLabel(index: number, loaded: number, hasMore: boolean): string {
  return `${index + 1} of ${loaded}${hasMore ? '+' : ''}`
}

// Whether to ask for the next page: the list goes on, a request is not already out, and the file shown is within PREFETCH_DISTANCE of the
// last one loaded. A page whose files cannot be shown loads nothing new, so this stays true and asks again, which walks past it.
export function shouldFetchMore(index: number, loaded: number, hasMore: boolean, fetching: boolean): boolean {
  return index >= 0 && hasMore && !fetching && loaded - index <= PREFETCH_DISTANCE
}
