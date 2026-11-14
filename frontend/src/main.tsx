import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router-dom'
import { ErrorBoundary } from './components/ErrorBoundary'
import { queryClient } from './lib/queryClient'
import { reloadForStaleChunk } from './lib/staleChunks'
import { setDeviceIdProvider, startSession } from './features/auth/session'
import { indexedDbDeviceStore } from './features/encryption/deviceStore'
import App from './App'
import './index.css'

// Asks the server whether this browser still has a working session. The access token is only kept in memory,
// so every page load starts without one. Routes show a loading state until this is answered.
setDeviceIdProvider(() => indexedDbDeviceStore.lastDeviceId())
void startSession()

// Fired when a script file this tab asks for no longer exists, which is what an update leaves behind for a tab that was already open.
window.addEventListener('vite:preloadError', (event) => {
  reloadForStaleChunk(event, { storage: () => window.sessionStorage, now: Date.now, reload: () => window.location.reload() })
})

// QueryClientProvider makes the shared cache available to every hook in the tree.
// BrowserRouter gives React Router access to the URL so Routes can render the
// right component for the current path.
createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ErrorBoundary>
      <QueryClientProvider client={queryClient}>
        <BrowserRouter>
          <App />
        </BrowserRouter>
      </QueryClientProvider>
    </ErrorBoundary>
  </StrictMode>,
)
