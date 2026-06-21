import { QueryClient } from '@tanstack/react-query'

// Shared QueryClient instance. Created once here and passed into QueryClientProvider
// in main.tsx so the same cache is accessible everywhere in the app.
export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 1000 * 60,  // cached data is considered fresh for 1 minute
      retry: 1,              // retry failed requests once before showing an error
    },
  },
})
