import { Logo } from '../../../components/Logo'

export function SelectConversation() {
  return (
    <div className="flex flex-1 flex-col items-center justify-center gap-3 p-8 text-center">
      <Logo tile size={64} />
      <h2 className="text-base font-semibold text-fg">Select a conversation</h2>
      <p className="max-w-xs text-sm text-fg-muted">
        Pick one from the list, or choose New conversation in the bar under it.
      </p>
    </div>
  )
}
