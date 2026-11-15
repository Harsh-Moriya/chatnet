import { MEDIA_NOTICES } from './callTexts'

// The microphone and the camera. Capturing is behind an interface so the controller can be driven without a browser.

export type MediaFailure = keyof typeof MEDIA_NOTICES

export class MediaUnavailable extends Error {
  readonly failure: MediaFailure

  constructor(failure: MediaFailure) {
    super(MEDIA_NOTICES[failure])
    this.name = 'MediaUnavailable'
    this.failure = failure
  }
}

export interface MediaSource {
  // The microphone, and the camera when `video` is true. Rejects with MediaUnavailable.
  capture: (video: boolean) => Promise<MediaStream>
  // A new track from a chosen device (to replace the one being sent). Rejects with MediaUnavailable.
  captureDevice: (kind: 'audio' | 'video', deviceId: string) => Promise<MediaStreamTrack>
}

function failureOf(error: unknown): MediaFailure {
  const name = error instanceof DOMException || error instanceof Error ? error.name : ''

  if (name === 'NotAllowedError' || name === 'SecurityError' || name === 'PermissionDeniedError') return 'denied'
  if (name === 'NotFoundError' || name === 'OverconstrainedError' || name === 'DevicesNotFoundError') return 'missing'
  if (name === 'NotReadableError' || name === 'AbortError' || name === 'TrackStartError') return 'busy'
  return 'other'
}

export const browserMedia: MediaSource = {
  async capture(video) {
    if (typeof navigator === 'undefined' || !navigator.mediaDevices?.getUserMedia) throw new MediaUnavailable('unsupported')

    try {
      return await navigator.mediaDevices.getUserMedia({
        audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true },
        video: video ? { facingMode: 'user' } : false,
      })
    } catch (error) {
      throw new MediaUnavailable(failureOf(error))
    }
  },

  async captureDevice(kind, deviceId) {
    if (typeof navigator === 'undefined' || !navigator.mediaDevices?.getUserMedia) throw new MediaUnavailable('unsupported')

    try {
      const stream = await navigator.mediaDevices.getUserMedia(
        kind === 'audio' ? { audio: { deviceId: { exact: deviceId } } } : { video: { deviceId: { exact: deviceId } } },
      )
      return stream.getTracks()[0]
    } catch (error) {
      throw new MediaUnavailable(failureOf(error))
    }
  },
}

export function stopStream(stream: MediaStream | null | undefined): void {
  stream?.getTracks().forEach((track) => track.stop())
}
