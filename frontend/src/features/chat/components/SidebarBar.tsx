import { MessagesSquare, Search, SquarePen } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { Tooltip } from '../../../components/ui/Tooltip'
import { cn } from '../../../lib/cn'
import { unreadLabel } from '../readState'
import { type SidebarView, viewAfterKey } from '../sidebarTabs'

const TABS = [
  { view: 'chats', label: 'Conversations', Icon: MessagesSquare },
  { view: 'new', label: 'New conversation', Icon: SquarePen },
  { view: 'search', label: 'Message search', Icon: Search },
] as const satisfies readonly { view: SidebarView; label: string; Icon: typeof Search }[]

interface SidebarBarProps {
  view: SidebarView
  // `key` when it was reached with the arrow keys, which must leave the focus on the bar.
  onChange: (view: SidebarView, source: 'click' | 'key') => void
  // Unread messages in conversations that are not muted, shown on the first icon while another panel hides the list.
  unread: number
}

// The three panels of the sidebar as a bar of icons at its foot: a pill in the flow of the sidebar, not over it, so it never covers the last
// row. The arrow keys move along it, and only the chosen icon is reached with Tab.
export function SidebarBar({ view, onChange, unread }: SidebarBarProps) {
  return (
    <div className="flex shrink-0 justify-center px-4 pb-[max(0.75rem,env(safe-area-inset-bottom))] pt-2">
      <div
        role="tablist"
        aria-label="Sidebar"
        className="flex gap-3 rounded-2xl border border-line bg-surface p-2 shadow-lg"
        onKeyDown={(event) => {
          const next = viewAfterKey(view, event.key)
          if (next === null) return

          event.preventDefault()
          onChange(next, 'key')
          event.currentTarget.querySelector<HTMLElement>(`#sidebar-tab-${next}`)?.focus()
        }}
      >
        {TABS.map(({ view: tab, label, Icon }) => {
          const selected = tab === view
          const badge = tab === 'chats' && !selected && unread > 0

          return (
            <Tooltip key={tab} side="top" label={label}>
              <Button
                id={`sidebar-tab-${tab}`}
                role="tab"
                variant="ghost"
                size="icon"
                aria-selected={selected}
                aria-controls={`sidebar-panel-${tab}`}
                aria-label={badge ? `${label}, ${unreadLabel(unread)} unread` : label}
                tabIndex={selected ? 0 : -1}
                className={cn('relative h-10! w-10! rounded-xl!', selected && 'bg-accent-soft! text-accent-fg!')}
                onClick={() => onChange(tab, 'click')}
              >
                <Icon className="h-5 w-5" aria-hidden="true" />
                {badge && (
                  <span
                    aria-hidden="true"
                    className="absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-blue-600 px-1 text-[10px] font-semibold text-white"
                  >
                    {unreadLabel(unread)}
                  </span>
                )}
              </Button>
            </Tooltip>
          )
        })}
      </div>
    </div>
  )
}
