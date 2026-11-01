import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { AlertCircle, X } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { Input } from '../../../components/ui/Input'
import { getApiError } from '../../../lib/errors'
import { useCreateDirect } from '../api/useCreateDirect'
import { useCreateGroup } from '../api/useCreateGroup'
import type { Conversation, UserSummary } from '../types'
import { PeoplePicker } from './PeoplePicker'

interface NewConversationDialogProps {
  open: boolean
  onClose: () => void
}

export function NewConversationDialog({ open, onClose }: NewConversationDialogProps) {
  return (
    <Dialog open={open} onClose={onClose} title="New conversation">
      <NewConversationForm onDone={onClose} />
    </Dialog>
  )
}

type Mode = 'direct' | 'group'

// Rendered only while the dialog is open (see Dialog), so every opening starts empty.
function NewConversationForm({ onDone }: { onDone: () => void }) {
  const navigate = useNavigate()
  const [mode, setMode] = useState<Mode>('direct')
  const [groupName, setGroupName] = useState('')
  const [members, setMembers] = useState<UserSummary[]>([])

  const createDirect = useCreateDirect()
  const createGroup = useCreateGroup()

  const busy = createDirect.isPending || createGroup.isPending
  const failure = createDirect.error ?? createGroup.error

  function changeMode(next: Mode) {
    setMode(next)
    createDirect.reset()
    createGroup.reset()
  }

  function openConversation(conversation: Conversation) {
    navigate(`/chat/${conversation.id}`)
    onDone()
  }

  function pick(person: UserSummary) {
    if (mode === 'direct') {
      createDirect.mutate(person.id, { onSuccess: openConversation })
      return
    }

    setMembers((current) =>
      current.some((m) => m.id === person.id) ? current.filter((m) => m.id !== person.id) : [...current, person],
    )
  }

  function createTheGroup() {
    createGroup.mutate(
      { name: groupName.trim(), memberIds: members.map((m) => m.id) },
      { onSuccess: openConversation },
    )
  }

  const selectedIds = new Set(members.map((m) => m.id))

  return (
    <div className="flex min-h-0 flex-col gap-4 p-5">
      <div className="flex gap-2" role="group" aria-label="Type of conversation">
        <Button
          variant={mode === 'direct' ? 'primary' : 'ghost'}
          size="sm"
          aria-pressed={mode === 'direct'}
          onClick={() => changeMode('direct')}
        >
          Direct message
        </Button>
        <Button
          variant={mode === 'group' ? 'primary' : 'ghost'}
          size="sm"
          aria-pressed={mode === 'group'}
          onClick={() => changeMode('group')}
        >
          New group
        </Button>
      </div>

      {failure && (
        <div role="alert" className="flex items-start gap-2 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-600">
          <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" />
          <span>{getApiError(failure)}</span>
        </div>
      )}

      {mode === 'group' && (
        <Input
          aria-label="Group name"
          placeholder="Group name"
          maxLength={100}
          value={groupName}
          onChange={(event) => setGroupName(event.target.value)}
        />
      )}

      {mode === 'group' && members.length > 0 && (
        <ul className="flex flex-wrap gap-2" aria-label="Selected members">
          {members.map((member) => (
            <li key={member.id} className="flex items-center gap-1 rounded-full bg-blue-50 py-1 pl-3 pr-1 text-xs text-blue-700">
              {member.displayName}
              <button
                type="button"
                aria-label={`Remove ${member.displayName}`}
                className="rounded-full p-1 hover:bg-blue-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
                onClick={() => pick(member)}
              >
                <X className="h-3 w-3" />
              </button>
            </li>
          ))}
        </ul>
      )}

      <PeoplePicker onPick={pick} selectedIds={mode === 'group' ? selectedIds : undefined} disabled={busy} />

      {mode === 'group' && (
        <Button
          fullWidth
          isLoading={createGroup.isPending}
          disabled={groupName.trim() === '' || members.length === 0}
          onClick={createTheGroup}
        >
          Create group{members.length > 0 ? ` with ${members.length} ${members.length === 1 ? 'person' : 'people'}` : ''}
        </Button>
      )}
    </div>
  )
}
