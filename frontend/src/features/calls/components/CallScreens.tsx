import { useChatConnection } from '../../chat/hooks/chatConnectionContext'
import { useCallStore } from '../callStore'
import { CallBar } from './CallBar'
import { CallSound } from './CallSound'
import { CallView } from './CallView'
import { IncomingCallDialog } from './IncomingCallDialog'

// The screens of a call: the one that asks whether to answer, the one that holds the call, and the bar it can be put away as. The two
// dialogs cannot be dismissed, so a call is only answered, declined or ended with its own buttons.
export function CallScreens() {
  const phase = useCallStore((state) => state.phase)
  const minimised = useCallStore((state) => state.minimised)
  const { calls } = useChatConnection()

  return (
    <>
      <IncomingCallDialog open={phase === 'incoming'} calls={calls} />
      <CallSound />
      {minimised && <CallBar calls={calls} />}
      <CallView open={phase !== 'incoming' && !minimised} calls={calls} />
    </>
  )
}
