import { AlertCircle, LoaderCircle, Maximize, Minimize, Pause, Play, Volume2, VolumeX } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'
import { Button } from '../../../components/ui/Button'
import { cn } from '../../../lib/cn'
import type { FileSource } from '../files/fileObjects'
import { useFileObject } from '../hooks/useFileObject'
import { videoStreams } from './openStreams'
import { CONTROLS_HIDE_MS, SEEK_STEP_SECONDS, bufferedEndAt, clampTime, formatTime, fractionOf, nextSpeed, takesText } from './videoView'
import type { VideoStream } from './videoStream'
import { ViewerButton } from './ViewerButton'

// How the video is got to the <video> element: asked of the service worker, which plays it from the encrypted file a range at a time; or,
// where there is no worker for this page (or the stream fails before anything loaded), by decrypting the whole file and playing that.
type Playback = { kind: 'opening' } | { kind: 'stream'; url: string } | { kind: 'whole' }

interface VideoViewerProps {
  source: FileSource
  name: string
  onDownload: () => void
}

const LIGHT_BUTTON = 'text-white hover:bg-white/15 active:bg-white/25 focus-visible:ring-offset-slate-900'

// One video, with controls of its own over the player: play and pause, a seek bar that shows what is loaded, the time, the volume, the speed
// and full screen. The keys are Space or K (play and pause), J and L (10 seconds back and forward), M (mute) and F (full screen); the arrow
// keys stay with the viewer, which moves to the previous and next file, except on the seek bar, where they seek.
export function VideoViewer({ source, name, onDownload }: VideoViewerProps) {
  const videoRef = useRef<HTMLVideoElement>(null)
  const stageRef = useRef<HTMLDivElement>(null)

  const [playback, setPlayback] = useState<Playback>({ kind: 'opening' })
  const [attempt, setAttempt] = useState(0)
  const [failed, setFailed] = useState(false)
  // The stream the worker was given, to let it go when playing is done. Whether the video has loaded yet decides what an error means.
  const stream = useRef<VideoStream | null>(null)
  const loaded = useRef(false)

  // The link of the file may be renewed while this is mounted, and the stream must not start over for it: it is opened for the file, with the
  // latest source.
  const latest = useRef(source)
  useEffect(() => {
    latest.current = source
  })

  const fileId = source.id
  useEffect(() => {
    let current = true
    loaded.current = false

    void videoStreams.open(latest.current).then((opened) => {
      if (!current) {
        opened?.stop()
        return
      }
      stream.current = opened
      setPlayback(opened ? { kind: 'stream', url: opened.url } : { kind: 'whole' })
    })

    return () => {
      current = false
      stream.current?.stop()
      stream.current = null
    }
  }, [fileId, attempt])

  const whole = playback.kind === 'whole'
  const [file, retryFile] = useFileObject(whole ? source : null, whole)
  const src = playback.kind === 'stream' ? playback.url : whole && file.status === 'ready' ? file.url : undefined

  const [paused, setPaused] = useState(true)
  const [time, setTime] = useState(0)
  const [duration, setDuration] = useState(0)
  const [buffered, setBuffered] = useState(0)
  const [waiting, setWaiting] = useState(false)
  const [volume, setVolume] = useState(1)
  const [muted, setMuted] = useState(false)
  const [rate, setRate] = useState(1)
  const [fullscreen, setFullscreen] = useState(false)
  const [ready, setReady] = useState(false)

  // The controls go away three seconds after the last movement while the video plays, and are there while it is paused.
  const [awake, setAwake] = useState(true)
  const hide = useRef<number | undefined>(undefined)
  const wake = useCallback(() => {
    setAwake(true)
    window.clearTimeout(hide.current)
    hide.current = window.setTimeout(() => setAwake(false), CONTROLS_HIDE_MS)
  }, [])
  useEffect(() => () => window.clearTimeout(hide.current), [])
  const controlsShown = paused || awake || !ready

  const togglePlay = useCallback(() => {
    const video = videoRef.current
    if (!video) return
    if (video.paused) void video.play().catch(() => undefined)
    else video.pause()
  }, [])

  const seekTo = useCallback((seconds: number) => {
    const video = videoRef.current
    if (video) video.currentTime = clampTime(seconds, video.duration)
  }, [])

  const toggleMute = useCallback(() => {
    const video = videoRef.current
    if (video) video.muted = !video.muted
  }, [])

  const toggleFullscreen = useCallback(() => {
    const stage = stageRef.current
    if (document.fullscreenElement) {
      void document.exitFullscreen()
    } else if (stage?.requestFullscreen) {
      void stage.requestFullscreen().catch(() => undefined)
    } else {
      // A phone that has no full screen for a page has one for the video.
      ;(videoRef.current as (HTMLVideoElement & { webkitEnterFullscreen?: () => void }) | null)?.webkitEnterFullscreen?.()
    }
  }, [])

  useEffect(() => {
    const onChange = () => setFullscreen(document.fullscreenElement !== null)
    document.addEventListener('fullscreenchange', onChange)
    return () => document.removeEventListener('fullscreenchange', onChange)
  }, [])

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.ctrlKey || event.metaKey || event.altKey) return
      // A key typed into a field, and a space on a button, are theirs. A slider is not a field: after a click on the seek bar the keys of the
      // player must still work.
      if (takesText(event.target as Element | null)) return
      if (event.key === ' ' && event.target instanceof HTMLButtonElement) return

      const key = event.key.toLowerCase()
      if (key === ' ' || key === 'k') togglePlay()
      else if (key === 'j') seekTo((videoRef.current?.currentTime ?? 0) - SEEK_STEP_SECONDS)
      else if (key === 'l') seekTo((videoRef.current?.currentTime ?? 0) + SEEK_STEP_SECONDS)
      else if (key === 'm') toggleMute()
      else if (key === 'f') toggleFullscreen()
      else return

      event.preventDefault()
      wake()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [togglePlay, seekTo, toggleMute, toggleFullscreen, wake])

  function onError() {
    // Before anything has loaded, a stream that failed is tried the other way once; after that, or later, the video cannot be played.
    if (!loaded.current && playback.kind === 'stream') {
      stream.current?.stop()
      stream.current = null
      setPlayback({ kind: 'whole' })
    } else {
      setFailed(true)
    }
  }

  function retry() {
    setFailed(false)
    setReady(false)
    setPlayback({ kind: 'opening' })
    setAttempt((n) => n + 1)
    retryFile()
  }

  function updateBuffered(video: HTMLVideoElement) {
    setBuffered(bufferedEndAt(video.buffered, video.currentTime))
  }

  const problem = failed || file.status === 'failed'
  const busy = !problem && (!ready || waiting)
  const played = fractionOf(time, duration)
  const loadedShare = fractionOf(buffered, duration)

  return (
    <div
      ref={stageRef}
      className={cn('relative flex min-h-0 min-w-0 flex-1 items-center justify-center bg-black', !controlsShown && !paused && 'cursor-none')}
      onPointerMove={wake}
      onPointerDown={wake}
      onFocus={wake}
    >
      <video
        ref={videoRef}
        src={src}
        aria-label={name}
        playsInline
        preload="metadata"
        className="h-full w-full object-contain"
        onClick={togglePlay}
        onDoubleClick={toggleFullscreen}
        onLoadedMetadata={(event) => {
          loaded.current = true
          setReady(true)
          setDuration(event.currentTarget.duration)
          void event.currentTarget.play().catch(() => undefined)
        }}
        onDurationChange={(event) => setDuration(event.currentTarget.duration)}
        onTimeUpdate={(event) => {
          setTime(event.currentTarget.currentTime)
          updateBuffered(event.currentTarget)
        }}
        onProgress={(event) => updateBuffered(event.currentTarget)}
        onPlay={() => setPaused(false)}
        onPause={() => setPaused(true)}
        onEnded={() => setPaused(true)}
        onWaiting={() => setWaiting(true)}
        onPlaying={() => setWaiting(false)}
        onCanPlay={() => setWaiting(false)}
        onVolumeChange={(event) => {
          setVolume(event.currentTarget.volume)
          setMuted(event.currentTarget.muted)
        }}
        onRateChange={(event) => setRate(event.currentTarget.playbackRate)}
        onError={onError}
      />

      {busy && (
        <div role="status" aria-label={`Loading ${name}`} className="pointer-events-none absolute inset-0 flex items-center justify-center">
          <LoaderCircle className="h-10 w-10 animate-spin text-slate-300" aria-hidden="true" />
        </div>
      )}

      {ready && paused && !problem && (
        <div className="pointer-events-none absolute inset-0 flex items-center justify-center">
          <span className="flex h-16 w-16 items-center justify-center rounded-full bg-slate-900/70 text-white">
            <Play className="h-8 w-8" aria-hidden="true" />
          </span>
        </div>
      )}

      {problem && (
        <div role="alert" className="absolute inset-0 flex flex-col items-center justify-center gap-3 bg-slate-900 p-6 text-center">
          <AlertCircle className="h-8 w-8 text-slate-300" aria-hidden="true" />
          <p className="max-w-xs text-sm text-slate-300">Couldn&apos;t play this video.</p>
          <div className="flex gap-2">
            <Button variant="ghost" size="sm" className={LIGHT_BUTTON} onClick={retry}>
              Try again
            </Button>
            <Button variant="ghost" size="sm" className={LIGHT_BUTTON} onClick={onDownload}>
              Download
            </Button>
          </div>
        </div>
      )}

      {ready && !problem && (
        <div
          className={cn(
            'absolute inset-x-0 bottom-0 bg-gradient-to-t from-slate-900/90 to-transparent px-3 pb-2 pt-8 transition-opacity',
            controlsShown ? 'opacity-100' : 'pointer-events-none opacity-0',
          )}
        >
          <div className="relative h-5">
            <div className="absolute inset-x-0 top-1/2 h-1 -translate-y-1/2 rounded-full bg-white/25">
              <div className="absolute inset-y-0 left-0 rounded-full bg-white/40" style={{ width: `${loadedShare * 100}%` }} />
              <div className="absolute inset-y-0 left-0 rounded-full bg-blue-500" style={{ width: `${played * 100}%` }} />
            </div>
            {/* The track is drawn above; this does the work of a slider (the pointer, the keys, a screen reader) and is not seen. */}
            <input
              type="range"
              aria-label="Seek"
              aria-valuetext={`${formatTime(time)} of ${formatTime(duration)}`}
              min={0}
              max={Number.isFinite(duration) ? duration : 0}
              step={1}
              value={Math.min(time, Number.isFinite(duration) ? duration : 0)}
              disabled={!Number.isFinite(duration) || duration <= 0}
              onChange={(event) => seekTo(Number(event.target.value))}
              className="absolute inset-0 h-full w-full cursor-pointer opacity-0 disabled:cursor-default"
            />
          </div>

          <div className="mt-1 flex items-center gap-1 text-white">
            <ViewerButton aria-label={paused ? 'Play' : 'Pause'} onClick={togglePlay}>
              {paused ? <Play className="h-5 w-5" /> : <Pause className="h-5 w-5" />}
            </ViewerButton>
            <span className="min-w-0 truncate px-1 text-xs tabular-nums" aria-hidden="true">
              {formatTime(time)} / {formatTime(duration)}
            </span>
            <span className="flex-1" />
            <ViewerButton aria-label={muted || volume === 0 ? 'Unmute' : 'Mute'} onClick={toggleMute}>
              {muted || volume === 0 ? <VolumeX className="h-5 w-5" /> : <Volume2 className="h-5 w-5" />}
            </ViewerButton>
            <input
              type="range"
              aria-label="Volume"
              min={0}
              max={1}
              step={0.05}
              value={muted ? 0 : volume}
              onChange={(event) => {
                const video = videoRef.current
                if (!video) return
                video.volume = Number(event.target.value)
                video.muted = Number(event.target.value) === 0
              }}
              className="hidden h-1 w-20 cursor-pointer accent-blue-500 sm:block"
            />
            <Button
              variant="ghost"
              size="sm"
              aria-label={`Playback speed ${rate}`}
              className={`min-w-12 ${LIGHT_BUTTON}`}
              onClick={() => {
                if (videoRef.current) videoRef.current.playbackRate = nextSpeed(videoRef.current.playbackRate)
              }}
            >
              {rate}×
            </Button>
            <ViewerButton aria-label={fullscreen ? 'Exit full screen' : 'Full screen'} onClick={toggleFullscreen}>
              {fullscreen ? <Minimize className="h-5 w-5" /> : <Maximize className="h-5 w-5" />}
            </ViewerButton>
          </div>
        </div>
      )}
    </div>
  )
}
