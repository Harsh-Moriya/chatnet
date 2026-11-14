import { defineConfig } from 'vite'

// The service worker is built apart from the page: the browser fetches it from a fixed address (/sw.js) and runs it as a classic script,
// so it is one self-contained file with no imports and no hash in its name. `npm run build` runs this after the build of the page, into
// the same folder; the dev server builds it when it is asked for (see vite.config.ts).
export default defineConfig({
  publicDir: false,
  build: {
    outDir: 'dist',
    emptyOutDir: false,
    copyPublicDir: false,
    target: 'es2022',
    lib: { entry: 'src/sw/main.ts', formats: ['iife'], name: 'ChatNetWorker', fileName: () => 'sw.js' },
  },
})
