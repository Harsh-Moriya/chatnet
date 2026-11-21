// The three panels of the sidebar, in the order of the bar at its foot.
export type SidebarView = 'chats' | 'new' | 'search'

export const SIDEBAR_VIEWS: readonly SidebarView[] = ['chats', 'new', 'search']

// Where the arrow keys on the bar go: right and left to the next and the previous, round the ends, Home and End to the first and the last.
// Null for any other key, which the bar leaves alone.
export function viewAfterKey(current: SidebarView, key: string): SidebarView | null {
  const index = SIDEBAR_VIEWS.indexOf(current)
  const count = SIDEBAR_VIEWS.length

  switch (key) {
    case 'ArrowRight':
      return SIDEBAR_VIEWS[(index + 1) % count]
    case 'ArrowLeft':
      return SIDEBAR_VIEWS[(index + count - 1) % count]
    case 'Home':
      return SIDEBAR_VIEWS[0]
    case 'End':
      return SIDEBAR_VIEWS[count - 1]
    default:
      return null
  }
}
