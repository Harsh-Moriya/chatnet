import { BellOff, BellRing } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { usePushSwitch } from '../hooks/usePushSwitch'
import { Alert } from '../../../components/ui/Alert'

// The switch for alerts while the application is closed, in the profile. It is left out until the server has said it can send them
// (a server without push keys has nothing to offer), and says why when this browser cannot.
export function PushSwitch() {
  const { state, busy, problem, turnOn, turnOff } = usePushSwitch()

  if (state === 'loading' || state === 'unavailable') return null

  return (
    <section aria-labelledby="push-heading" className="space-y-2 border-t border-line pt-4">
      <h3 id="push-heading" className="text-sm font-medium text-fg-soft">
        Alerts when the app is closed
      </h3>

      {state === 'unsupported' && <p className="text-xs text-fg-muted">This browser cannot show alerts while the app is closed.</p>}

      {state === 'blocked' && (
        <p className="text-xs text-fg-muted">
          Alerts are blocked for this site. Allow notifications in your browser settings to turn them on.
        </p>
      )}

      {(state === 'off' || state === 'on') && (
        <div className="flex items-center justify-between gap-3">
          <p className="text-xs text-fg-muted">
            {state === 'on'
              ? 'This browser gets an alert for a message, a mention or an invitation when you are away.'
              : 'Get an alert in this browser for a message, a mention or an invitation when you are away.'}
          </p>
          <Button
            variant="ghost"
            size="sm"
            disabled={busy}
            onClick={() => void (state === 'on' ? turnOff() : turnOn())}
          >
            {state === 'on' ? <BellOff className="h-3.5 w-3.5" /> : <BellRing className="h-3.5 w-3.5" />}
            {state === 'on' ? 'Turn off' : 'Turn on'}
          </Button>
        </div>
      )}

      {problem && (
        <Alert role="alert" size="sm">{problem}</Alert>
      )}
    </section>
  )
}
