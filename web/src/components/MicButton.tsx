import { useEffect, useRef, useState } from 'react'
import { ApiError, type Api, type VoiceLanguage } from '../api'
import { t } from '../i18n'

type State = 'idle' | 'recording' | 'transcribing'

const MAX_SECONDS = 60

/**
 * Push to talk: click (or Alt+M) to start, click again to stop. The recording is sent to the shell, which returns text
 * that is only put into the prompt box for the user to review and send (ADR 0013: voice is a channel, not an authority).
 */
export default function MicButton({ api, language, onText }: { api: Api; language: VoiceLanguage; onText: (text: string) => void }) {
  const [state, setState] = useState<State>('idle')
  const [error, setError] = useState<string | null>(null)
  const recorder = useRef<MediaRecorder | null>(null)
  const chunks = useRef<Blob[]>([])
  const timer = useRef<number | undefined>(undefined)

  const supported = typeof MediaRecorder !== 'undefined' && !!navigator.mediaDevices?.getUserMedia

  const stop = () => {
    window.clearTimeout(timer.current)
    if (recorder.current?.state === 'recording') recorder.current.stop()
  }

  const start = async () => {
    setError(null)
    let stream: MediaStream
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true } })
    } catch {
      setError(t('Microphone access was blocked. Allow it in the browser to dictate.'))
      return
    }
    const rec = new MediaRecorder(stream)
    chunks.current = []
    rec.ondataavailable = (e) => e.data.size > 0 && chunks.current.push(e.data)
    rec.onstop = async () => {
      stream.getTracks().forEach((it) => it.stop())
      const blob = new Blob(chunks.current, { type: rec.mimeType })
      if (blob.size < 1500) {
        setState('idle')
        setError(t('Nothing was recorded. Hold the button a little longer.'))
        return
      }
      setState('transcribing')
      try {
        const result = await api.transcribe(blob, language)
        if (result.text.trim()) onText(result.text.trim())
        else setError(t('No speech was recognised.'))
      } catch (e) {
        setError(
          e instanceof ApiError && e.status === 503
            ? t('Voice is unavailable right now. Type your question instead.')
            : e instanceof ApiError && e.status === 413
              ? t('The recording is too long.')
              : t('Could not transcribe the recording. Try again or type.'),
        )
      } finally {
        setState('idle')
      }
    }
    recorder.current = rec
    rec.start()
    setState('recording')
    timer.current = window.setTimeout(stop, MAX_SECONDS * 1000)
  }

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.altKey && e.key.toLowerCase() === 'm' && state !== 'transcribing') {
        e.preventDefault()
        if (state === 'recording') stop()
        else void start()
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [state, language])

  useEffect(() => () => stop(), [])

  if (!supported) return null

  return (
    <>
      {error && (
        <span role="alert" className="error">
          {error}
        </span>
      )}
      <button
        type="button"
        className={`btn mic ${state}`}
        aria-pressed={state === 'recording'}
        disabled={state === 'transcribing'}
        title={t('Dictate (Alt+M)')}
        onClick={() => (state === 'recording' ? stop() : void start())}
      >
        {state === 'idle' && 'Dictate'}
        {state === 'recording' && 'Stop'}
        {state === 'transcribing' && t('Transcribing…')}
      </button>
    </>
  )
}
