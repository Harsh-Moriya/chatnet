import { useMutation } from '@tanstack/react-query'
import { createDirect } from './chatApi'

// Find-or-create: asking for the same person again returns the conversation that already exists.
export function useCreateDirect() {
  return useMutation({ mutationFn: (otherUserId: string) => createDirect(otherUserId) })
}
