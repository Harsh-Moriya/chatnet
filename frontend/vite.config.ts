import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    proxy: {
      // Forwards any /api request to the backend during development.
      // This means the frontend can call /api/auth/login without specifying a host,
      // and Vite rewrites it to http://localhost:5045/api/auth/login behind the scenes.
      // In production the reverse proxy (nginx / Azure / Railway) does the same job.
      '/api': 'http://localhost:5045',
    },
  },
})
