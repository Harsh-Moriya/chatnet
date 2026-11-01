import { type FormEvent, useState } from 'react'
import { AlertCircle, Crown, LogOut, ShieldCheck, User, UserMinus, UserPlus, X } from 'lucide-react'
import { Avatar } from '../../../components/ui/Avatar'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { Input } from '../../../components/ui/Input'
import { Menu, type MenuItem } from '../../../components/ui/Menu'
import { getApiError } from '../../../lib/errors'
import { useAuth } from '../../auth/hooks/useAuth'
import { addMembers, changeMemberRole, leaveConversation, removeMember, renameConversation } from '../api/chatApi'
import { MAX_GROUP_NAME_LENGTH, canManage, memberActions, roleOf, roomLeft } from '../groupRules'
import { useChatStore } from '../store/chatStore'
import type { Conversation, Member, UserSummary } from '../types'
import { PeoplePicker } from './PeoplePicker'

interface GroupInfoDialogProps {
  conversation: Conversation
  open: boolean
  onClose: () => void
}

export function GroupInfoDialog({ conversation, open, onClose }: GroupInfoDialogProps) {
  return (
    <Dialog open={open} onClose={onClose} title="Group info">
      <GroupInfo conversation={conversation} onDone={onClose} />
    </Dialog>
  )
}

// What the reader is being asked to confirm before it happens: a change that is hard to take back.
type Confirmation = { kind: 'owner' | 'remove'; member: Member } | { kind: 'leave' }

// Rendered only while the dialog is open (see Dialog), so every opening starts as the group is.
function GroupInfo({ conversation, onDone }: { conversation: Conversation; onDone: () => void }) {
  const { user } = useAuth()
  const selfId = user?.userId ?? ''
  const members = useChatStore((state) => state.members[conversation.id])
  const role = roleOf(members, selfId)
  const manager = canManage(role)

  const [name, setName] = useState(conversation.name)
  const [adding, setAdding] = useState(false)
  const [chosen, setChosen] = useState<UserSummary[]>([])
  const [confirming, setConfirming] = useState<Confirmation | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // What every change needs: the dialog is busy while it runs, a refusal is shown, and nothing on the screen changes until
  // the server has accepted it (the store takes the server's answer).
  async function run(action: () => Promise<unknown>): Promise<boolean> {
    setBusy(true)
    try {
      await action()
      setError(null)
      return true
    } catch (failure) {
      setError(getApiError(failure))
      return false
    } finally {
      setBusy(false)
    }
  }

  const trimmed = name.trim()
  const canRename = trimmed !== '' && trimmed !== conversation.name

  async function rename(event: FormEvent) {
    event.preventDefault()
    if (canRename) await run(() => renameConversation(conversation.id, trimmed))
  }

  function itemsFor(target: Member): MenuItem[] {
    return memberActions(role, target, selfId).map((action): MenuItem => {
      switch (action) {
        case 'makeAdmin':
          return {
            label: 'Make admin',
            icon: <ShieldCheck className="h-4 w-4" />,
            onSelect: () => void run(() => changeMemberRole(conversation.id, target.userId, 'Admin')),
          }
        case 'makeMember':
          return {
            label: 'Make member',
            icon: <User className="h-4 w-4" />,
            onSelect: () => void run(() => changeMemberRole(conversation.id, target.userId, 'Member')),
          }
        case 'makeOwner':
          return {
            label: 'Make owner',
            icon: <Crown className="h-4 w-4" />,
            onSelect: () => setConfirming({ kind: 'owner', member: target }),
          }
        case 'remove':
          return {
            label: 'Remove from group',
            icon: <UserMinus className="h-4 w-4" />,
            onSelect: () => setConfirming({ kind: 'remove', member: target }),
          }
      }
    })
  }

  async function confirm() {
    if (!confirming) return

    const done = await run(() => {
      if (confirming.kind === 'owner') return changeMemberRole(conversation.id, confirming.member.userId, 'Owner')
      if (confirming.kind === 'remove') return removeMember(conversation.id, confirming.member.userId)
      return leaveConversation(conversation.id)
    })

    if (!done) return
    setConfirming(null)
    if (confirming.kind === 'leave') onDone()
  }

  const memberIds = new Set((members ?? []).map((member) => member.userId))
  const room = roomLeft(memberIds.size)
  const chosenIds = new Set(chosen.map((person) => person.id))

  function togglePerson(person: UserSummary) {
    setChosen((current) => {
      if (current.some((p) => p.id === person.id)) return current.filter((p) => p.id !== person.id)
      return current.length < room ? [...current, person] : current
    })
  }

  async function addChosen() {
    const done = await run(() => addMembers(conversation.id, chosen.map((person) => person.id)))
    if (!done) return

    setChosen([])
    setAdding(false)
  }

  return (
    <div className="flex min-h-0 flex-col gap-4 overflow-y-auto p-5">
      {error && (
        <div role="alert" className="flex items-start gap-2 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-600">
          <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" />
          <span>{error}</span>
        </div>
      )}

      {manager ? (
        <form onSubmit={(event) => void rename(event)} className="flex items-start gap-2">
          <Input
            aria-label="Group name"
            maxLength={MAX_GROUP_NAME_LENGTH}
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
          <Button type="submit" disabled={!canRename || busy}>
            Save
          </Button>
        </form>
      ) : (
        <h3 className="truncate text-base font-semibold text-slate-900">{conversation.name}</h3>
      )}

      <section aria-label="People in this group">
        <h3 className="mb-1 text-xs font-semibold uppercase tracking-wide text-slate-500">
          {members ? `${members.length} ${members.length === 1 ? 'person' : 'people'}` : 'People'}
        </h3>

        {!members && <p className="py-3 text-sm text-slate-500">Loading people…</p>}

        <ul className="divide-y divide-line-subtle">
          {(members ?? []).map((member) => {
            const items = itemsFor(member)

            return (
              <li key={member.userId} className="flex items-center gap-3 py-2">
                <Avatar name={member.displayName} src={member.avatarUrl} />
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-medium text-slate-900">
                    {member.displayName}
                    {member.userId === selfId && <span className="font-normal text-slate-500"> (you)</span>}
                  </p>
                  <p className="truncate text-xs text-slate-500">@{member.userName}</p>
                </div>
                {member.role !== 'Member' && (
                  <span className="rounded-full bg-blue-50 px-2 py-0.5 text-xs font-medium text-blue-700">
                    {member.role}
                  </span>
                )}
                {items.length > 0 && <Menu label={`Actions for ${member.displayName}`} items={items} />}
              </li>
            )
          })}
        </ul>
      </section>

      {manager && !adding && (
        <Button variant="ghost" disabled={busy || room === 0} onClick={() => setAdding(true)}>
          <UserPlus className="h-4 w-4" />
          {room === 0 ? 'The group is full' : 'Add people'}
        </Button>
      )}

      {manager && adding && (
        <section aria-label="Add people" className="flex min-h-0 flex-col gap-3 rounded-lg border border-line p-3">
          {chosen.length > 0 && (
            <ul className="flex flex-wrap gap-2" aria-label="People to add">
              {chosen.map((person) => (
                <li key={person.id} className="flex items-center gap-1 rounded-full bg-blue-50 py-1 pl-3 pr-1 text-xs text-blue-700">
                  {person.displayName}
                  <button
                    type="button"
                    aria-label={`Do not add ${person.displayName}`}
                    className="rounded-full p-1 hover:bg-blue-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
                    onClick={() => togglePerson(person)}
                  >
                    <X className="h-3 w-3" />
                  </button>
                </li>
              ))}
            </ul>
          )}

          <PeoplePicker onPick={togglePerson} selectedIds={chosenIds} excludeIds={memberIds} disabled={busy} />

          <p className="text-xs text-slate-500">{room} more {room === 1 ? 'person fits' : 'people fit'} in this group.</p>

          <div className="flex gap-2">
            <Button
              isLoading={busy}
              disabled={chosen.length === 0}
              onClick={() => void addChosen()}
            >
              Add {chosen.length > 0 ? `${chosen.length} ${chosen.length === 1 ? 'person' : 'people'}` : 'people'}
            </Button>
            <Button
              variant="ghost"
              disabled={busy}
              onClick={() => {
                setAdding(false)
                setChosen([])
              }}
            >
              Cancel
            </Button>
          </div>
        </section>
      )}

      {confirming && (
        <div role="alertdialog" aria-label="Confirm" className="rounded-lg border border-line bg-slate-50 p-3">
          <p className="text-sm text-slate-900">{confirmationText(confirming, role === 'Owner')}</p>
          <div className="mt-3 flex gap-2">
            <Button size="sm" isLoading={busy} onClick={() => void confirm()}>
              {confirming.kind === 'leave' ? 'Leave' : confirming.kind === 'remove' ? 'Remove' : 'Make owner'}
            </Button>
            <Button size="sm" variant="ghost" disabled={busy} onClick={() => setConfirming(null)}>
              Cancel
            </Button>
          </div>
        </div>
      )}

      {!confirming && (
        <Button variant="ghost" className="text-red-500" disabled={busy} onClick={() => setConfirming({ kind: 'leave' })}>
          <LogOut className="h-4 w-4" />
          Leave group
        </Button>
      )}
    </div>
  )
}

function confirmationText(confirming: Confirmation, readerIsOwner: boolean): string {
  switch (confirming.kind) {
    case 'owner':
      return `Make ${confirming.member.displayName} the owner? You will become an admin.`
    case 'remove':
      return `Remove ${confirming.member.displayName} from the group? They can still read what was said until now, but not write.`
    case 'leave':
      return readerIsOwner
        ? 'Leave this group? It goes to the admin, or else the member, who has been in it longest. You can still read what was said until now, but not write.'
        : 'Leave this group? You can still read what was said until now, but not write.'
  }
}
