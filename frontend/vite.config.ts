import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
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
