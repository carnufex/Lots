import { useEffect, useRef, useState } from 'react'
import { ApiError, type Api } from '../api'

type State = 'idle' | 'loading' | 'playing'

const AUTO_KEY = 'lots.autospeak'

function readAuto(): boolean {
  try {
    return localStorage.getItem(AUTO_KEY) === '1'
  } catch {
    return false
  }
}

/**
 * Reads the final answer of a run aloud. Only the final answer can be spoken: the server has no free text-to-speech
 * (ADR 0013), so tool output and approval requests never reach the speech provider.
 */
export default function SpeakButton({ api, runId }: { api: Api; runId: string }) {
  const [state, setState] = useState<State>('idle')
  const [error, setError] = useState<string | null>(null)
  const [auto, setAuto] = useState(readAuto)
  const audio = useRef<HTMLAudioElement | null>(null)
  const url = useRef<string | null>(null)
  const alive = useRef(true)

  const stop = () => {
    audio.current?.pause()
    audio.current = null
    if (url.current) URL.revokeObjectURL(url.current)
    url.current = null
    if (alive.current) setState('idle')
  }

  const play = async () => {
    stop()
    setError(null)
    setState('loading')
    try {
      const blob = await api.speak(runId, 'auto')
      if (!alive.current) return
      url.current = URL.createObjectURL(blob)
      const el = new Audio(url.current)
      el.onended = stop
      audio.current = el
      await el.play()
      setState('playing')
    } catch (e) {
      setState('idle')
      setError(
        e instanceof ApiError && e.status === 503
          ? 'Voice is unavailable right now. The answer is above in text.'
          : e instanceof DOMException && e.name === 'NotAllowedError'
            ? 'The browser blocked playback. Press Listen once to allow it.'
            : 'Could not read the answer aloud.',
      )
    }
  }

  useEffect(() => {
    alive.current = true
    if (auto) void play()
    return () => {
      alive.current = false
      audio.current?.pause()
      if (url.current) URL.revokeObjectURL(url.current)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [runId])

  const toggleAuto = (on: boolean) => {
    setAuto(on)
    try {
      localStorage.setItem(AUTO_KEY, on ? '1' : '0')
    } catch {
      /* ignore: a per-viewer convenience */
    }
  }

  return (
    <div className="speak">
      <button type="button" className="btn" disabled={state === 'loading'} onClick={() => (state === 'playing' ? stop() : void play())}>
        {state === 'idle' && 'Listen'}
        {state === 'loading' && 'Preparing…'}
        {state === 'playing' && 'Stop'}
      </button>
      <label className="muted">
        <input type="checkbox" checked={auto} onChange={(e) => toggleAuto(e.target.checked)} /> Read answers aloud automatically
      </label>
      {error && (
        <span role="alert" className="error">
          {error}
        </span>
      )}
    </div>
  )
}
