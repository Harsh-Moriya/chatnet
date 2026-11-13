import { useState } from 'react'
import { TriangleAlert } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { useAuth } from '../../auth/hooks/useAuth'
import { useChatStore } from '../../chat/store/chatStore'
import { useIdentityChanges } from '../identityChanges'
import { LazySafetyNumberDialog } from './LazySafetyNumberDialog'

// Above a conversation, for each person in it whose security key this browser has seen change: nothing is sealed to them and what they
// sign is not trusted until the person looking has compared the safety number with them and accepted the new key.
export function KeyChangedBanner({ conversationId }: { conversationId: string }) {
  const { user } = useAuth()
  const members = useChatStore((state) => state.members[conversationId])
  const changed = useIdentityChanges((state) => state.changed)
  const [comparing, setComparing] = useState<string | null>(null)

  const people = (members ?? []).filter((member) => member.userId !== user?.userId && member.userId in changed)
  if (people.length === 0) return null

  return (
    <div role="alert" className="shrink-0 space-y-1 border-b border-amber-200 bg-amber-50 px-4 py-2 text-sm text-amber-800">
      {people.map((person) => (
        <div key={person.userId} className="flex flex-wrap items-center gap-x-3 gap-y-1">
          <TriangleAlert className="h-4 w-4 shrink-0" aria-hidden="true" />
          <span className="min-w-0 flex-1">
            {person.displayName}&apos;s security key changed. Compare the safety number with them before you accept it.
          </span>
          <Button variant="ghost" size="sm" onClick={() => setComparing(person.userId)}>
            Compare and accept
          </Button>
          {comparing === person.userId && (
            <LazySafetyNumberDialog contactId={person.userId} contactName={person.displayName} open onClose={() => setComparing(null)} />
          )}
        </div>
      ))}
    </div>
  )
}
