import { useEffect, useState } from 'react'
import { Button } from '../../../components/ui/Button'
import { formatLinkCode } from '../../../lib/crypto'
import { linkThisDevice } from '../controller'
import type { LinkAttempt } from '../flows'
import { describeFailure } from '../messages'
import { Problem } from './Problem'

const QR_SIZE = 176

// The new device shows a code and a QR code and waits for a device that is signed in to approve. The request lasts a few minutes;
// when it ends, or fails, the person starts again from here.
export function LinkNewDevicePanel({ onCancel }: { onCancel: () => void }) {
  const [attempt, setAttempt] = useState<LinkAttempt | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [qr, setQr] = useState<string | null>(null)
  // Changing it (see startAgain) starts a new request.
  const [round, setRound] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    let current = true

    linkThisDevice(controller.signal).then(
      (started) => {
        if (!current) return
        setAttempt(started)
        // The keys arrive in the store when it succeeds, which replaces this panel; only a failure is shown.
        started.result.catch((failure: unknown) => {
          if (current) setProblem(describeFailure(failure))
        })
      },
      (failure: unknown) => {
        if (current) setProblem(describeFailure(failure))
      },
    )

    return () => {
      current = false
      controller.abort()
    }
  }, [round])

  // A new request, from nothing: the old code and picture must not stay on screen while the new one is asked for.
  function startAgain() {
    setAttempt(null)
    setProblem(null)
    setQr(null)
    setRound((n) => n + 1)
  }

  const uri = attempt?.uri
  useEffect(() => {
    if (uri === undefined) return
    let current = true

    // Loaded when it is first needed, so the page does not carry the QR code library until a person links a device.
    void import('qrcode')
      .then((qrcode) => qrcode.toDataURL(uri, { margin: 1, width: QR_SIZE, errorCorrectionLevel: 'M' }))
      .then((image) => {
        if (current) setQr(image)
      })
      // The code beside it works without the picture.
      .catch(() => undefined)

    return () => {
      current = false
    }
  }, [uri])

  return (
    <div className="space-y-3">
      <Problem message={problem} />

      {attempt && problem === null && (
        <>
          <p className="text-xs text-slate-500">
            On a device where you are signed in, open your profile and enter this code under Link code. This browser gets your keys as
            soon as you approve.
          </p>
          <p
            aria-label="Link code"
            className="rounded-lg border border-line bg-slate-50 px-3 py-3 text-center font-mono text-2xl font-semibold tracking-widest text-slate-900"
          >
            {formatLinkCode(attempt.code)}
          </p>
          {qr && <img src={qr} width={QR_SIZE} height={QR_SIZE} alt="QR code with the same request" className="mx-auto rounded-lg border border-line" />}
          <p className="text-xs text-slate-500">
            Waiting for approval. The code works until{' '}
            {new Date(attempt.expiresAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}.
          </p>
        </>
      )}

      {attempt === null && problem === null && <p className="text-xs text-slate-500">Asking for a code...</p>}

      <div className="flex gap-2">
        {problem !== null && (
          <Button size="sm" onClick={startAgain}>
            Try again
          </Button>
        )}
        <Button variant="ghost" size="sm" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </div>
  )
}
