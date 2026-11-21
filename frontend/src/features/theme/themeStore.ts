import { create } from 'zustand'
import { type ThemeChoice, applyTheme, readChoice, rememberChoice, resolveTheme } from './themePreference'

interface ThemeState {
  choice: ThemeChoice
  setChoice: (choice: ThemeChoice) => void
}

export const useThemeStore = create<ThemeState>()((set) => ({
  choice: readChoice(),
  setChoice: (choice) => {
    rememberChoice(choice)
    set({ choice })
  },
}))

// Called once, before the page renders. It puts the theme on the page, again whenever the choice changes, and while the choice is "system"
// whenever the device switches between light and dark. (The script in index.html has already done the first of these before the first draw.)
export function startTheme(): void {
  const system = window.matchMedia('(prefers-color-scheme: dark)')
  const apply = () =>
    applyTheme(
      document.documentElement,
      document.querySelector<HTMLMetaElement>('meta[name="theme-color"]'),
      resolveTheme(useThemeStore.getState().choice, system.matches),
    )

  apply()
  useThemeStore.subscribe(apply)
  system.addEventListener('change', apply)
}
