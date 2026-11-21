import { Alert } from '../../../components/ui/Alert'

// The red banner of these screens: the same one the profile uses, for a step that failed.
export function Problem({ message }: { message: string | null }) {
  if (message === null) return null

  return (
    <Alert role="alert" size="sm">{message}</Alert>
  )
}
