import { defineConfig } from 'vitest/config'
import { build, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// Serves the service worker in development. In a build it is made apart (vite.sw.config.ts, run by `npm run build`); here it is built from
// its source each time the browser asks for it, so that a change shows at once and the page and the worker never disagree.
function serviceWorker(): Plugin {
  return {
    name: 'chatnet-service-worker',
    apply: 'serve',
    configureServer(server) {
      server.middlewares.use('/sw.js', (_request, response) => {
        build({ configFile: 'vite.sw.config.ts', logLevel: 'silent', build: { write: false } }).then(
          (result) => {
            const output = (Array.isArray(result) ? result[0] : result) as { output: { type: string; code?: string }[] }
            response.setHeader('Content-Type', 'text/javascript')
            response.setHeader('Cache-Control', 'no-cache')
            response.end(output.output.find((chunk) => chunk.type === 'chunk')?.code ?? '')
          },
          (error: unknown) => {
            response.statusCode = 500
            response.end(String(error))
          },
        )
      })
    },
  }
}

export default defineConfig({
  plugins: [react(), tailwindcss(), serviceWorker()],
  // Tests cover logic only (stores, event handlers, timers), so they run in plain Node with no
  // DOM. Components are checked in a real browser.
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
  },
  server: {
    proxy: {
      // Forwards any /api request to the backend during development.
      // This means the frontend can call /api/auth/login without specifying a host,
      // and Vite rewrites it to http://localhost:5045/api/auth/login behind the scenes.
      // In production the reverse proxy (nginx / Azure / Railway) does the same job.
      '/api': 'http://localhost:5045',
      // ws: true lets the proxy forward the WebSocket upgrade that SignalR uses.
      // Without it the connection silently falls back to long polling.
      '/hubs': {
        target: 'http://localhost:5045',
        ws: true,
      },
    },
  },
})
