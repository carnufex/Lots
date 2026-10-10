import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, type Api, type UserSettings, type VoiceLanguage } from '../api'
import type { VoiceConfig } from '../config'
import { initialLanguage } from '../voice/language'
import VocabularyEditor from '../components/VocabularyEditor'

const MIN_SECONDS = 8
const MAX_SECONDS = 30

const SCRIPT: Record<'sv' | 'en', string> = {
  sv: 'Hej, det här är min röst. Jag pratar lugnt och tydligt, som om jag berättade något för en kollega. Idag har jag tittat på servrarna, läst lite loggar och druckit för mycket kaffe. Nu ska vi se om allt fungerar som det ska.',
  en: 'Hello, this is my voice. I am speaking calmly and clearly, as if I were telling a colleague something. Today I looked at the servers, read some logs and drank too much coffee. Now let us see whether everything works as it should.',
}

type Slider = { key: keyof Pick<UserSettings, 'talkativeness' | 'warmth' | 'formality' | 'expressiveness' | 'pace'>; label: string; low: string; high: string }

const PERSONALITY: Slider[] = [
  { key: 'talkativeness', label: 'Talkativeness', low: 'Brief', high: 'Chatty' },
  { key: 'warmth', label: 'Warmth', low: 'Neutral', high: 'Warm' },
  { key: 'formality', label: 'Formality', low: 'Casual', high: 'Formal' },
]
const VOICE: Slider[] = [
  { key: 'expressiveness', label: 'Expressiveness', low: 'Calm', high: 'Lively' },
  { key: 'pace', label: 'Pace', low: 'Faster', high: 'Slower, more careful' },
]

/**
 * Personal voice and personality (ADR 0015): how the agent talks to you in text and speech, and recording your own voice for it
 * to use. The recording is yours: used only when the agent speaks to you, never shared, deleted here at any time.
 */
export default function VoicePage({ api, voice }: { api: Api; voice: VoiceConfig }) {
  const [settings, setSettings] = useState<UserSettings | null>(null)
  const [draft, setDraft] = useState<UserSettings | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    let cancelled = false
    api
      .getSettings()
      .then((s) => {
        if (cancelled) return
        setSettings(s)
        setDraft(s)
      })
      .catch(() => !cancelled && setError('Could not load your settings.'))
    return () => {
      cancelled = true
    }
  }, [api])

  const dirty = !!settings && !!draft && (['talkativeness', 'warmth', 'formality', 'expressiveness', 'pace'] as const).some((k) => settings[k] !== draft[k])

  const save = async () => {
    if (!draft) return
    setBusy(true)
    setError(null)
    try {
      const s = await api.putSettings(draft)
      setSettings(s)
      setDraft(s)
      setSaved(true)
      window.setTimeout(() => setSaved(false), 2500)
    } catch {
      setError('Could not save your settings.')
    } finally {
      setBusy(false)
    }
  }

  const preview = async () => {
    // Plays a fixed phrase in your voice with the values currently saved (there is no free text-to-speech, ADR 0013).
    setError(null)
    const blob = await api.ack(initialLanguage(voice.defaultLanguage) === 'en' ? 'en' : 'sv')
    if (!blob) return setError('Voice is unavailable right now.')
    const url = URL.createObjectURL(blob)
    const audio = new Audio(url)
    audio.onended = () => URL.revokeObjectURL(url)
    void audio.play()
  }

  if (!draft || !settings) return <p className="muted">{error ?? 'Loading…'}</p>

  return (
    <div className="voicepage">
      <h1>Voice and personality</h1>

      <section className="card">
        <h2>Personality</h2>
        <p className="muted small">
          How the agent talks to you, in text and when speaking. These are style preferences only: they never change what the agent is
          allowed to do.
        </p>
        {PERSONALITY.map((s) => (
          <SliderRow key={s.key} slider={s} value={draft[s.key]} onChange={(v) => setDraft({ ...draft, [s.key]: v })} />
        ))}
      </section>

      <section className="card">
        <h2>Voice</h2>
        {voice.enabled ? (
          <>
            {VOICE.map((s) => (
              <SliderRow key={s.key} slider={s} value={draft[s.key]} onChange={(v) => setDraft({ ...draft, [s.key]: v })} />
            ))}
            <p className="muted small">
              Expressiveness and pace apply to the expressive voice; the fast fallback voice ignores them.
            </p>
          </>
        ) : (
          <p className="muted">Voice is not configured on this installation.</p>
        )}
        <div className="row">
          <button type="button" className="btn primary" disabled={!dirty || busy} onClick={() => void save()}>
            Save
          </button>
          {voice.enabled && (
            <button type="button" className="btn" disabled={dirty} onClick={() => void preview()} title={dirty ? 'Save first, then listen' : undefined}>
              Listen
            </button>
          )}
          {saved && <span className="muted small" role="status">Saved</span>}
        </div>
      </section>

      {voice.enabled && (
        <OwnVoice
          api={api}
          current={settings.ownVoice}
          onChange={(s) => {
            setSettings(s)
            setDraft((d) => (d ? { ...d, ownVoice: s.ownVoice } : s))
          }}
          setError={setError}
        />
      )}

      {voice.enabled && (
        <section className="card">
          <h2>Dictation vocabulary</h2>
          <VocabularyEditor api={api} />
        </section>
      )}

      {error && (
        <p role="alert" className="error small">
          {error}
        </p>
      )}
    </div>
  )
}

function SliderRow({ slider, value, onChange }: { slider: Slider; value: number; onChange: (v: number) => void }) {
  return (
    <label className="sliderrow">
      <span className="sliderlabel">{slider.label}</span>
      <span className="muted small">{slider.low}</span>
      <input type="range" min={0} max={100} step={5} value={value} onChange={(e) => onChange(Number(e.target.value))} aria-label={slider.label} />
      <span className="muted small">{slider.high}</span>
    </label>
  )
}

type Phase = 'idle' | 'recording' | 'review'

function OwnVoice({
  api,
  current,
  onChange,
  setError,
}: {
  api: Api
  current: UserSettings['ownVoice']
  onChange: (s: UserSettings) => void
  setError: (m: string | null) => void
}) {
  const [phase, setPhase] = useState<Phase>('idle')
  const [language, setLanguage] = useState<Exclude<VoiceLanguage, 'auto'>>('sv')
  const [seconds, setSeconds] = useState(0)
  const [level, setLevel] = useState(0)
  const [clip, setClip] = useState<Blob | null>(null)
  const [clipUrl, setClipUrl] = useState<string | null>(null)
  const [consent, setConsent] = useState(false)
  const [busy, setBusy] = useState(false)

  const recorder = useRef<MediaRecorder | null>(null)
  const chunks = useRef<Blob[]>([])
  const stream = useRef<MediaStream | null>(null)
  const timer = useRef(0)
  const raf = useRef(0)
  const startedAt = useRef(0)

  const release = useCallback(() => {
    window.clearInterval(timer.current)
    cancelAnimationFrame(raf.current)
    stream.current?.getTracks().forEach((t) => t.stop())
    stream.current = null
  }, [])

  useEffect(
    () => () => {
      release()
      if (clipUrl) URL.revokeObjectURL(clipUrl)
    },
    [release, clipUrl],
  )

  const stop = useCallback(() => {
    if (recorder.current?.state === 'recording') recorder.current.stop()
  }, [])

  const start = async () => {
    setError(null)
    try {
      const s = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: false, noiseSuppression: true, autoGainControl: true } })
      stream.current = s
      const ctx = new AudioContext()
      const analyser = ctx.createAnalyser()
      analyser.fftSize = 1024
      ctx.createMediaStreamSource(s).connect(analyser)
      const buf = new Float32Array(analyser.fftSize)
      const tick = () => {
        analyser.getFloatTimeDomainData(buf)
        let sum = 0
        for (const x of buf) sum += x * x
        setLevel(Math.min(1, Math.sqrt(sum / buf.length) * 8))
        raf.current = requestAnimationFrame(tick)
      }
      tick()

      chunks.current = []
      const r = new MediaRecorder(s)
      recorder.current = r
      r.ondataavailable = (e) => e.data.size > 0 && chunks.current.push(e.data)
      r.onstop = () => {
        release()
        void ctx.close()
        const blob = new Blob(chunks.current, { type: r.mimeType || 'audio/webm' })
        setClip(blob)
        setClipUrl(URL.createObjectURL(blob))
        setConsent(false)
        setPhase('review')
      }
      r.start()
      startedAt.current = Date.now()
      setSeconds(0)
      setPhase('recording')
      timer.current = window.setInterval(() => {
        const t = (Date.now() - startedAt.current) / 1000
        setSeconds(t)
        if (t >= MAX_SECONDS) stop()
      }, 200)
    } catch {
      release()
      setError('Microphone access was blocked. Allow it in the browser to record your voice.')
    }
  }

  const useClip = async () => {
    if (!clip) return
    setBusy(true)
    setError(null)
    try {
      onChange(await api.recordVoice(clip, consent))
      setPhase('idle')
      setClip(null)
      setClipUrl(null)
    } catch (e) {
      setError(
        e instanceof ApiError && e.status === 400
          ? 'That recording could not be used (too short, too quiet or unreadable). Record 10–30 seconds in a quiet room and try again.'
          : 'Could not save your voice. Try again.',
      )
    } finally {
      setBusy(false)
    }
  }

  const remove = async () => {
    setBusy(true)
    setError(null)
    try {
      onChange(await api.deleteVoice())
    } catch {
      setError('Could not delete your voice. Try again.')
    } finally {
      setBusy(false)
    }
  }

  const tooShort = seconds < MIN_SECONDS

  return (
    <section className="card">
      <h2>Your own voice</h2>
      <p className="muted small">
        Record yourself and the agent speaks with your voice when it talks to you. The recording is used only for your own conversations, is never
        shared with other users and you can delete it here at any time. Only record your own voice.
      </p>

      {current && phase === 'idle' && (
        <div className="row">
          <span className="chip">Your voice is active · {Math.round(current.seconds)} s</span>
          <button type="button" className="btn" disabled={busy} onClick={() => void remove()}>
            Delete my voice
          </button>
        </div>
      )}

      {phase === 'idle' && (
        <div className="row">
          <select aria-label="Reading language" value={language} onChange={(e) => setLanguage(e.target.value as 'sv' | 'en')}>
            <option value="sv">Read in Swedish</option>
            <option value="en">Read in English</option>
          </select>
          <button type="button" className="btn primary" onClick={() => void start()}>
            {current ? 'Record again' : 'Record my voice'}
          </button>
        </div>
      )}

      {phase !== 'idle' && (
        <blockquote className="script" aria-label="Text to read">
          {SCRIPT[language]}
        </blockquote>
      )}

      {phase === 'recording' && (
        <div className="row">
          <div className="meter" aria-hidden="true">
            <div className="meterfill" style={{ width: `${Math.round(level * 100)}%` }} />
          </div>
          <span className="mono" role="timer">
            {seconds.toFixed(0)} / {MAX_SECONDS} s
          </span>
          <button type="button" className="btn primary" onClick={stop} disabled={tooShort} title={tooShort ? `Keep reading, at least ${MIN_SECONDS} s` : undefined}>
            Stop
          </button>
        </div>
      )}

      {phase === 'review' && clipUrl && (
        <>
          <audio controls src={clipUrl} aria-label="Your recording" />
          <label className="consent">
            <input type="checkbox" checked={consent} onChange={(e) => setConsent(e.target.checked)} />
            This is my own voice, and I agree that Lots stores it to speak to me with it.
          </label>
          <div className="row">
            <button type="button" className="btn primary" disabled={!consent || busy} onClick={() => void useClip()}>
              Use this voice
            </button>
            <button type="button" className="btn" disabled={busy} onClick={() => setPhase('idle')}>
              Discard
            </button>
          </div>
        </>
      )}
    </section>
  )
}
