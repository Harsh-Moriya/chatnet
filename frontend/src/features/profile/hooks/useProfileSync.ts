import { useQuery } from '@tanstack/react-query'
import { useAuthStore } from '../../auth/store/authStore'
import { fetchMe } from '../api/profileApi'

// Loads the signed-in user's profile once the session is known. A session response carries the name but not the picture,
// and fetchMe puts what it gets into the stores itself. Loaded again after a reconnect, because a change pushed while the
// connection was down was lost.
export function useProfileSync() {
  const userId = useAuthStore((state) => state.user?.userId)

  useQuery({
    queryKey: ['me', userId],
    queryFn: ({ signal }) => fetchMe(signal),
    enabled: userId !== undefined,
    staleTime: Infinity,
  })
}
