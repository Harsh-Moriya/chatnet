import { Eye, EyeOff } from 'lucide-react'
import { Button } from '../../../components/ui/Button'

// The eye at the end of a password field that shows the password or hides it again.
export function PasswordToggle({ shown, onToggle }: { shown: boolean; onToggle: () => void }) {
  return (
    <Button type="button" variant="ghost" size="icon-sm" aria-label={shown ? 'Hide password' : 'Show password'} onClick={onToggle}>
      {shown ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
    </Button>
  )
}
