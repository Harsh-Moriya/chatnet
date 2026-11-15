import type { IceServer } from './types'
import type { CandidateInit } from './signals'

// The WebRTC connection of a call, behind an interface so the controller is driven without a browser. The controller decides when to make an
// offer, whose signature to check and when to add a candidate; this only does what the browser's RTCPeerConnection does.

export type PeerState = 'connecting' | 'connected' | 'disconnected' | 'failed' | 'closed'

export interface PeerHandlers {
  // A local ICE candidate was found: sent to the other side as it comes (trickle).
  onCandidate: (candidate: CandidateInit) => void
  onTrack: (stream: MediaStream) => void
  onState: (state: PeerState) => void
}

export interface PeerSession {
  addTrack: (track: MediaStreamTrack, stream: MediaStream) => void
  // Makes the offer and sets it as the local description; returns its session description.
  createOffer: () => Promise<string>
  // Takes the other side's offer, makes the answer and sets it; returns the answer's session description.
  acceptOffer: (sdp: string) => Promise<string>
  acceptAnswer: (sdp: string) => Promise<void>
  // Starts the connection again with new candidates (after the network changed): the servers given are the ones to use from now on, and the
  // offer returned is for the other side to answer, like the first.
  restartOffer: (iceServers: IceServer[]) => Promise<string>
  addCandidate: (candidate: CandidateInit) => Promise<void>
  // Sends this track instead of the one of the same kind that is being sent.
  replaceTrack: (kind: 'audio' | 'video', track: MediaStreamTrack) => Promise<void>
  close: () => void
}

export type PeerFactory = (iceServers: IceServer[], handlers: PeerHandlers) => PeerSession

export const createRtcPeer: PeerFactory = (iceServers, handlers) => {
  const connection = new RTCPeerConnection({ iceServers })

  connection.onicecandidate = (event) => {
    if (event.candidate === null) return

    const { candidate, sdpMid, sdpMLineIndex, usernameFragment } = event.candidate
    handlers.onCandidate({ candidate, sdpMid, sdpMLineIndex, usernameFragment })
  }

  connection.ontrack = (event) => handlers.onTrack(event.streams[0] ?? new MediaStream([event.track]))

  connection.onconnectionstatechange = () => {
    const state = connection.connectionState
    handlers.onState(state === 'new' ? 'connecting' : state)
  }

  const localSdp = (): string => {
    const description = connection.localDescription
    if (description === null) throw new Error('The connection has no local description.')
    return description.sdp
  }

  return {
    addTrack: (track, stream) => void connection.addTrack(track, stream),

    async createOffer() {
      await connection.setLocalDescription(await connection.createOffer())
      return localSdp()
    },

    async acceptOffer(sdp) {
      await connection.setRemoteDescription({ type: 'offer', sdp })
      await connection.setLocalDescription(await connection.createAnswer())
      return localSdp()
    },

    acceptAnswer: (sdp) => connection.setRemoteDescription({ type: 'answer', sdp }),

    async restartOffer(iceServers) {
      connection.setConfiguration({ ...connection.getConfiguration(), iceServers })
      await connection.setLocalDescription(await connection.createOffer({ iceRestart: true }))
      return localSdp()
    },

    addCandidate: (candidate) => connection.addIceCandidate(candidate),

    async replaceTrack(kind, track) {
      const sender = connection.getSenders().find((candidate) => candidate.track?.kind === kind)
      if (sender) await sender.replaceTrack(track)
    },

    close: () => connection.close(),
  }
}
