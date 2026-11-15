import { useChatConnection } from '../../chat/hooks/chatConnectionContext'
import { useCallStore } from '../callStore'
import { CallView } from './CallView'
import { IncomingCallDialog } from './IncomingCallDialog'

// The two screens of a call: the one that asks whether to answer, and the one that holds the call. Neither can be dismissed, so a call
// is only answered, declined or ended with its own buttons.
export function CallScreens() {
  const phase = useCallStore((state) => state.phase)
  const { calls } = useChatConnection()

  return (
    <>
      <IncomingCallDialog open={phase === 'incoming'} calls={calls} />
      <CallView open={phase !== 'incoming'} calls={calls} />
    </>
  )
}
