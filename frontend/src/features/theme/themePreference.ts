// How this browser looks: the person's choice (light, dark, or whatever the device is set to) and what it comes to. The choice is kept here, in the
// browser, for everyone who uses it, signed in or not: the look of a screen is the device's, and nothing about it is on the server.

export type ThemeChoice = 'system' | 'light' | 'dark'
export type ResolvedTheme = 'light' | 'dark'

export type ThemeStorage = Pick<Storage, 'getItem' | 'setItem'>

// The same key is read by the script in index.html, which runs before the page is drawn.
export const THEME_KEY = 'chatnet-theme'

const CHOICES: readonly ThemeChoice[] = ['system', 'light', 'dark']

// Storage can be unavailable (private windows, blocked site data). Then the page is light, and a choice lasts until it is closed.
function browserStorage(): ThemeStorage | null {
  try {
    return localStorage
  } catch {
    return null
  }
}

// Light unless the person chose otherwise: a missing, unknown or unreadable value is light.
export function readChoice(storage: ThemeStorage | null = browserStorage()): ThemeChoice {
  try {
    const stored = storage?.getItem(THEME_KEY)
    return CHOICES.find((choice) => choice === stored) ?? 'light'
  } catch {
    return 'light'
  }
}

export function rememberChoice(choice: ThemeChoice, storage: ThemeStorage | null = browserStorage()): void {
  try {
    storage?.setItem(THEME_KEY, choice)
  } catch {
    // Nothing to do: the choice applies until the page is closed.
  }
}

export function resolveTheme(choice: ThemeChoice, systemPrefersDark: boolean): ResolvedTheme {
  if (choice === 'system') return systemPrefersDark ? 'dark' : 'light'
  return choice
}

// The colour a phone or a browser gives its own bar around the page.
export const THEME_COLOR: Record<ResolvedTheme, string> = { light: '#2563eb', dark: '#0f172a' }

// Puts the theme on the page: the class every dark rule keys on, and the colour of the browser's bar.
export function applyTheme(
  root: { classList: { toggle: (name: string, force: boolean) => unknown } },
  themeColor: { content: string } | null,
  theme: ResolvedTheme,
): void {
  root.classList.toggle('dark', theme === 'dark')
  if (themeColor) themeColor.content = THEME_COLOR[theme]
}
