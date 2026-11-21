import { useCallStore } from '../callStore'
import { useStream } from '../useStream'

// The other person's voice. It lives outside the call screen so the sound carries on when the screen is put away as a bar, and the picture
// on the screen is muted so it is not heard twice.
export function CallSound() {
  const remoteStream = useCallStore((state) => state.remoteStream)
  const ref = useStream<HTMLAudioElement>(remoteStream)

  return <audio ref={ref} autoPlay />
}
