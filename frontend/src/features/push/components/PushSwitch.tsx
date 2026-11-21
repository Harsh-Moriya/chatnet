import { BellOff, BellRing } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { Tooltip } from '../../../components/ui/Tooltip'
import { cn } from '../../../lib/cn'
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
          <p className="min-w-0 text-xs text-fg-muted">
            {state === 'on'
              ? 'This browser gets an alert for a message, a mention or an invitation when you are away.'
              : 'Get an alert in this browser for a message, a mention or an invitation when you are away.'}
          </p>
          {/* An icon, so it never wraps on a narrow screen: the words beside it already say what it is for, and its colour says whether it is on. */}
          <Tooltip label={state === 'on' ? 'Turn alerts off' : 'Turn alerts on'}>
            <Button
              variant="ghost"
              size="icon"
              aria-label="Alerts when the app is closed"
              aria-pressed={state === 'on'}
              disabled={busy}
              onClick={() => void (state === 'on' ? turnOff() : turnOn())}
              className={cn(state === 'on' ? 'bg-accent-soft! text-accent-fg! hover:bg-accent-soft-strong!' : 'text-fg-muted!')}
            >
              {state === 'on' ? <BellRing className="h-5 w-5" /> : <BellOff className="h-5 w-5" />}
            </Button>
          </Tooltip>
        </div>
      )}

      {problem && (
        <Alert role="alert" size="sm">{problem}</Alert>
      )}
    </section>
  )
}
