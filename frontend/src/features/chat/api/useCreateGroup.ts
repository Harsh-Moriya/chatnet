import { useMutation } from '@tanstack/react-query'
import { createGroup } from './chatApi'

interface CreateGroupInput {
  name: string
  memberIds: string[]
}

export function useCreateGroup() {
  return useMutation({ mutationFn: ({ name, memberIds }: CreateGroupInput) => createGroup(name, memberIds) })
}
