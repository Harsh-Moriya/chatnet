import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { defineConfig, globalIgnores } from 'eslint/config'

export default defineConfig([
  globalIgnores(['dist']),
  {
    // A lint rule is fixed in the code, not switched off beside it: a directive comment has no effect, and the lint script fails on the
    // warning that says so.
    linterOptions: { noInlineConfig: true },
  },
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      reactHooks.configs.flat.recommended,
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      globals: globals.browser,
    },
  },
  {
    // The service worker runs in a worker, not a page: its globals are the worker's.
    files: ['src/sw/main.ts'],
    languageOptions: {
      globals: globals.serviceworker,
    },
  },
])
