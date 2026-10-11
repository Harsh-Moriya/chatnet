import { Component, type ErrorInfo, type ReactNode } from 'react'
import { AlertCircle } from 'lucide-react'
import { Button } from './ui/Button'

interface Props {
  children: ReactNode
}

interface State {
  failed: boolean
}

// Without this, an error thrown while rendering any component unmounts the whole app and leaves a
// blank page. This catches it and offers a reload instead. It has to be a class: React has no hook
// for catching render errors.
export class ErrorBoundary extends Component<Props, State> {
  state: State = { failed: false }

  static getDerivedStateFromError(): State {
    return { failed: true }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    // The browser console is where a developer looks; the page itself shows no technical detail.
    console.error('Unhandled error while rendering', error, info.componentStack)
  }

  render() {
    if (!this.state.failed) return this.props.children

    return (
      <div className="flex min-h-screen flex-col items-center justify-center gap-4 bg-slate-50 p-8 text-center">
        <AlertCircle className="h-10 w-10 text-slate-400" />
        <h1 className="text-lg font-semibold text-slate-900">Something went wrong</h1>
        <p className="max-w-sm text-sm text-slate-500">
          The page ran into a problem and could not continue. Reloading usually fixes it.
        </p>
        <Button onClick={() => window.location.reload()}>Reload the page</Button>
      </div>
    )
  }
}
