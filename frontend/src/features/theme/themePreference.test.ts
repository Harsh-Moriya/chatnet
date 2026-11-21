import { describe, expect, it } from 'vitest'
import { THEME_COLOR, THEME_KEY, type ThemeStorage, applyTheme, readChoice, rememberChoice, resolveTheme } from './themePreference'

function memoryStorage(initial?: string): ThemeStorage & { entries: Map<string, string> } {
  const entries = new Map<string, string>(initial === undefined ? [] : [[THEME_KEY, initial]])
  return {
    entries,
    getItem: (key) => entries.get(key) ?? null,
    setItem: (key, value) => void entries.set(key, value),
  }
}

const unavailable: ThemeStorage = {
  getItem: () => {
    throw new Error('blocked')
  },
  setItem: () => {
    throw new Error('blocked')
  },
}

describe('the theme this browser was last given', () => {
  it('is light until someone chooses otherwise', () => {
    expect(readChoice(memoryStorage())).toBe('light')
    expect(readChoice(null)).toBe('light')
  })

  it.each(['system', 'light', 'dark'] as const)('remembers %s', (choice) => {
    const storage = memoryStorage()

    rememberChoice(choice, storage)

    expect(readChoice(storage)).toBe(choice)
  })

  it('takes a value it does not know for light, so a stale or edited entry cannot leave the page in no theme', () => {
    expect(readChoice(memoryStorage('blue'))).toBe('light')
    expect(readChoice(memoryStorage(''))).toBe('light')
  })

  it('is light when the storage cannot be read, and a choice that cannot be kept is not an error', () => {
    expect(readChoice(unavailable)).toBe('light')
    expect(() => rememberChoice('dark', unavailable)).not.toThrow()
  })
})

describe('what a choice comes to', () => {
  it.each([
    ['light', false, 'light'],
    ['light', true, 'light'],
    ['dark', false, 'dark'],
    ['dark', true, 'dark'],
    ['system', false, 'light'],
    ['system', true, 'dark'],
  ] as const)('%s on a device that prefers dark=%s is %s', (choice, prefersDark, expected) => {
    expect(resolveTheme(choice, prefersDark)).toBe(expected)
  })
})

describe('putting the theme on the page', () => {
  function page() {
    const classes = new Set<string>()
    return {
      classes,
      root: { classList: { toggle: (name: string, force: boolean) => (force ? classes.add(name) : classes.delete(name)) } },
      color: { content: '' },
    }
  }

  it('adds the dark class and the dark bar colour for dark, and removes both for light', () => {
    const { classes, root, color } = page()

    applyTheme(root, color, 'dark')
    expect(classes.has('dark')).toBe(true)
    expect(color.content).toBe(THEME_COLOR.dark)

    applyTheme(root, color, 'light')
    expect(classes.has('dark')).toBe(false)
    expect(color.content).toBe(THEME_COLOR.light)
  })

  it('works on a page that has no theme-color tag', () => {
    const { classes, root } = page()

    applyTheme(root, null, 'dark')

    expect(classes.has('dark')).toBe(true)
  })
})
