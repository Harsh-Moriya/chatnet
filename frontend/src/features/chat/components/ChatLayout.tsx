import { Outlet, useMatch } from 'react-router-dom'
import { cn } from '../../../lib/cn'
import { Sidebar } from './Sidebar'

// Two panes side by side on a wide screen. On a phone there is room for one: the list, or the open
// conversation, chosen by the URL. Both stay mounted, only hidden, so the list keeps its data and
// scroll position while a conversation is open.
export function ChatLayout() {
  const conversationOpen = useMatch('/chat/:conversationId') !== null

  return (
    <div className="flex h-dvh bg-slate-50">
      <aside
        className={cn(
          'w-full shrink-0 flex-col border-r border-slate-200 bg-white md:flex md:w-80 lg:w-96',
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
