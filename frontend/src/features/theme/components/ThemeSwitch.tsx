import { Monitor, Moon, Sun } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { useThemeStore } from '../themeStore'
import type { ThemeChoice } from '../themePreference'

const OPTIONS: { value: ThemeChoice; label: string; Icon: typeof Sun }[] = [
  { value: 'system', label: 'System', Icon: Monitor },
  { value: 'light', label: 'Light', Icon: Sun },
  { value: 'dark', label: 'Dark', Icon: Moon },
]

// How this browser looks, in the profile. It is kept in the browser (see themePreference.ts), not with the account.
export function ThemeSwitch() {
  const choice = useThemeStore((state) => state.choice)
  const setChoice = useThemeStore((state) => state.setChoice)

  return (
    <section aria-labelledby="theme-heading" className="space-y-2 border-t border-line pt-4">
      <h3 id="theme-heading" className="text-sm font-medium text-fg-soft">
        Appearance
      </h3>
      <div role="group" aria-labelledby="theme-heading" className="grid grid-cols-3 gap-2">
        {OPTIONS.map(({ value, label, Icon }) => (
          <Button
            key={value}
            variant={choice === value ? 'primary' : 'secondary'}
            size="sm"
            aria-pressed={choice === value}
            onClick={() => setChoice(value)}
          >
            <Icon className="h-3.5 w-3.5" aria-hidden="true" />
            {label}
          </Button>
        ))}
      </div>
      <p className="text-xs text-fg-muted">
        {choice === 'system' ? 'Follows your device, light or dark.' : 'Kept on this browser.'}
      </p>
    </section>
  )
}
