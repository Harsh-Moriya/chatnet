import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { AlertCircle, Check, Search, X } from 'lucide-react'
import { Avatar } from '../../../components/ui/Avatar'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { Input } from '../../../components/ui/Input'
import { cn } from '../../../lib/cn'
import { getApiError } from '../../../lib/errors'
import { useDebouncedValue } from '../../../lib/useDebouncedValue'
import { useCreateDirect } from '../api/useCreateDirect'
import { useCreateGroup } from '../api/useCreateGroup'
import { useUserSearch } from '../api/useUserSearch'
import type { Conversation, UserSummary } from '../types'

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
  const [term, setTerm] = useState('')
  const [groupName, setGroupName] = useState('')
  const [members, setMembers] = useState<UserSummary[]>([])

  const search = useUserSearch(useDebouncedValue(term, 250))
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

  const enoughToSearch = term.trim().length >= 2
  const results = search.data ?? []

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

      <Input
        aria-label="Search people"
        placeholder="Search by name or username"
        leftIcon={<Search className="h-4 w-4" />}
        value={term}
        onChange={(event) => setTerm(event.target.value)}
      />

      <div className="min-h-24 flex-1 overflow-y-auto">
        {!enoughToSearch && <p className="py-4 text-center text-sm text-slate-500">Type at least 2 letters.</p>}

        {enoughToSearch && search.isError && (
          <p className="py-4 text-center text-sm text-red-500">{getApiError(search.error)}</p>
        )}

        {enoughToSearch && !search.isError && results.length === 0 && (
          <p className="py-4 text-center text-sm text-slate-500">{search.isFetching ? 'Searching…' : 'No one found.'}</p>
        )}

        {enoughToSearch && results.length > 0 && (
          <ul className={cn('transition-opacity', search.isFetching && 'opacity-60')}>
            {results.map((person) => {
              const selected = members.some((m) => m.id === person.id)

              return (
                <li key={person.id}>
                  <button
                    type="button"
                    disabled={busy}
                    onClick={() => pick(person)}
                    aria-pressed={mode === 'group' ? selected : undefined}
                    className="flex w-full items-center gap-3 rounded-lg px-2 py-2 text-left transition-colors hover:bg-slate-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 disabled:opacity-50"
                  >
                    <Avatar name={person.displayName} src={person.avatarUrl} />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-sm font-medium text-slate-900">{person.displayName}</span>
                      <span className="block truncate text-xs text-slate-500">@{person.userName}</span>
                    </span>
                    {selected && <Check className="h-4 w-4 shrink-0 text-blue-600" />}
                  </button>
                </li>
              )
            })}
          </ul>
        )}
      </div>

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
