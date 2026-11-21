import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { AlertCircle, X } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { Input } from '../../../components/ui/Input'
import { getApiError } from '../../../lib/errors'
import { describeKeyFailure } from '../../encryption/keyFailures'
import { useCreateDirect } from '../api/useCreateDirect'
import { useCreateGroup } from '../api/useCreateGroup'
import { contactIdsOf } from '../peopleOrder'
import { useChatStore } from '../store/chatStore'
import type { Conversation, UserSummary } from '../types'
import { PeoplePicker } from './PeoplePicker'

interface NewConversationPanelProps {
  // Called once a conversation has been made and opened, so the sidebar can show its list again.
  onDone: () => void
  // A search carried over from the list of conversations ("Find someone new"); a new `round` puts it in the box.
  seed?: { term: string; round: number }
}

// Starting a conversation: search for people, tick one for a direct conversation or several for a group. The panel stays in the sidebar while
// other panels are shown, so what was typed and ticked is still there when it comes back.
export function NewConversationPanel({ onDone, seed }: NewConversationPanelProps) {
  const navigate = useNavigate()
  const conversations = useChatStore((state) => state.conversations)
  const [members, setMembers] = useState<UserSummary[]>([])
  const [groupName, setGroupName] = useState('')
  // Counts the times it was emptied after a conversation was made, so the search box starts again too.
  const [round, setRound] = useState(0)

  const createDirect = useCreateDirect()
  const createGroup = useCreateGroup()

  const busy = createDirect.isPending || createGroup.isPending
  const failure = createDirect.error ?? createGroup.error
  const isGroup = members.length >= 2

  function toggle(person: UserSummary) {
    createDirect.reset()
    createGroup.reset()
    setMembers((current) => (current.some((m) => m.id === person.id) ? current.filter((m) => m.id !== person.id) : [...current, person]))
  }

  function openConversation(conversation: Conversation) {
    navigate(`/chat/${conversation.id}`)
    setMembers([])
    setGroupName('')
    setRound((current) => current + 1)
    onDone()
  }

  function start() {
    if (isGroup) createGroup.mutate({ name: groupName.trim(), memberIds: members.map((m) => m.id) }, { onSuccess: openConversation })
    else createDirect.mutate(members[0].id, { onSuccess: openConversation })
  }

  const selectedIds = new Set(members.map((m) => m.id))
  const nameOf = (userId: string) => members.find((p) => p.id === userId)?.displayName ?? 'Someone'

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-3 p-4">
      {failure && (
        <div role="alert" className="flex items-start gap-2 rounded-lg border border-danger-line bg-danger-soft px-3 py-2 text-sm text-danger-fg">
          <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" />
          <span>{describeKeyFailure(failure, nameOf) ?? getApiError(failure)}</span>
        </div>
      )}

      <PeoplePicker
        key={`${round}:${seed?.round ?? 0}`}
        initialTerm={seed?.term ?? ''}
        onPick={toggle}
        selectedIds={selectedIds}
        contactIds={contactIdsOf(conversations)}
        disabled={busy}
      />

      {members.length > 0 && (
        <div className="flex shrink-0 flex-col gap-3 border-t border-line pt-3">
          <ul className="flex flex-wrap gap-2" aria-label="Selected people">
            {members.map((member) => (
              <li key={member.id} className="flex items-center gap-1 rounded-full bg-accent-soft py-1 pl-3 pr-1 text-xs text-accent-fg">
                {member.displayName}
                <button
                  type="button"
                  aria-label={`Remove ${member.displayName}`}
                  className="rounded-full p-1 hover:bg-accent-soft-strong focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
                  onClick={() => toggle(member)}
                >
                  <X className="h-3 w-3" />
                </button>
              </li>
            ))}
          </ul>

          {isGroup && (
            <Input
              aria-label="Group name"
              placeholder="Group name"
              maxLength={100}
              value={groupName}
              onChange={(event) => setGroupName(event.target.value)}
            />
          )}

          <Button
            fullWidth
            isLoading={busy}
            disabled={isGroup && groupName.trim() === ''}
            onClick={start}
          >
            {isGroup ? `Create group with ${members.length} people` : `Message ${members[0].displayName}`}
          </Button>
        </div>
      )}
    </div>
  )
}
