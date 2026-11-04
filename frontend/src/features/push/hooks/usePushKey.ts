import { useQuery } from '@tanstack/react-query'
import { fetchPushKey } from '../api/pushApi'

// The server's push key, asked for once while someone is signed in. The data is null when the server has no push keys, which is an
// answer and not a failure, so nothing is retried for it. The cache is emptied when the person signs out.
export function usePushKey(enabled: boolean) {
  return useQuery({
    queryKey: ['push-key'],
    queryFn: ({ signal }) => fetchPushKey(signal),
    enabled,
    staleTime: Infinity,
  })
}
