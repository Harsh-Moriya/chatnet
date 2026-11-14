import { renewAttachmentLink } from '../files/openFiles'
import { createVideoStreams } from './videoStream'

// The page's streams, wired to the browser: the service worker that controls this page, the messages it sends, and the API for a new link.
export const videoStreams = createVideoStreams({
  controller: () => (typeof navigator !== 'undefined' && 'serviceWorker' in navigator ? navigator.serviceWorker.controller : null),
  listen: (listener) => {
    const onMessage = (event: MessageEvent) => listener({ data: event.data, ports: event.ports })
    navigator.serviceWorker.addEventListener('message', onMessage)
    return () => navigator.serviceWorker.removeEventListener('message', onMessage)
  },
  renewLink: renewAttachmentLink,
  newId: () => crypto.randomUUID(),
})
