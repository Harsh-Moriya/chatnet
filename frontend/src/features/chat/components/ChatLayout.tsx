import { Outlet, useMatch } from 'react-router-dom'
import { cn } from '../../../lib/cn'
import { useDocumentTitle } from '../../notifications/useDocumentTitle'
import { useContactPresence } from '../api/useContactPresence'
import { Sidebar } from './Sidebar'

// Two panes side by side on a wide screen. On a phone there is room for one: the list, or the open
// conversation, chosen by the URL. Both stay mounted, only hidden, so the list keeps its data and
// scroll position while a conversation is open.
export function ChatLayout() {
  const conversationOpen = useMatch('/chat/:conversationId') !== null

  // Who is online, for the dots in the list and the line under a conversation's name. Loaded here, once
  // for the whole chat; the changes after that arrive as pushes.
  useContactPresence()

  // The number of unread messages in the tab's title, so a background tab shows it.
  useDocumentTitle()

  return (
    <div className="flex h-dvh bg-slate-50">
      {/* Relative, so the notification panel can cover the list below the sidebar's header. */}
      <aside
        className={cn(
          'relative w-full shrink-0 flex-col border-r border-line bg-white md:flex md:w-80 lg:w-96',
          conversationOpen ? 'hidden' : 'flex',
        )}
      >
        <Sidebar />
      </aside>

      <main className={cn('min-w-0 flex-1 flex-col md:flex', conversationOpen ? 'flex' : 'hidden')}>
        <Outlet />
      </main>
    </div>
  )
}
