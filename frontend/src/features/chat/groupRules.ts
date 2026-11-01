import type { Member } from './types'

type Role = Member['role']

// The most people a group holds at once, as the server counts them: its owner and up to 100 others.
export const MAX_GROUP_MEMBERS = 101

export const MAX_GROUP_NAME_LENGTH = 100

// What the reader may be offered for another member of a group. The server decides what is allowed; these only keep the
// screen from offering what it would refuse.
export type MemberAction = 'makeAdmin' | 'makeMember' | 'makeOwner' | 'remove'

// The owner and the admins may add people and rename the group.
export function canManage(role: Role | undefined): boolean {
  return role === 'Owner' || role === 'Admin'
}

// The reader's own role in a group whose people are loaded; undefined while they are not.
export function roleOf(members: Member[] | undefined, userId: string | undefined): Role | undefined {
  return members?.find((member) => member.userId === userId)?.role
}

// The owner changes roles and removes anyone; an admin removes plain members. Nobody is offered anything for themselves
// (leaving is its own button).
export function memberActions(viewerRole: Role | undefined, target: Member, viewerId: string): MemberAction[] {
  if (target.userId === viewerId) return []

  if (viewerRole === 'Owner') {
    return [target.role === 'Admin' ? 'makeMember' : 'makeAdmin', 'makeOwner', 'remove']
  }

  if (viewerRole === 'Admin' && target.role === 'Member') return ['remove']

  return []
}

// How many more people fit in a group that has this many.
export function roomLeft(memberCount: number): number {
  return Math.max(0, MAX_GROUP_MEMBERS - memberCount)
}
