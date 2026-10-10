import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, type Api, type UserSettings, type VoiceLanguage } from '../api'
import type { VoiceConfig } from '../config'
import { initialLanguage } from '../voice/language'
import { PASSAGES } from '../voice/passages'
import VocabularyEditor from '../components/VocabularyEditor'
import { fmt, t } from '../i18n'

const MIN_SECONDS = 8
const MAX_SECONDS = 30

type Slider = { key: keyof Pick<UserSettings, 'talkativeness' | 'warmth' | 'formality' | 'expressiveness' | 'pace'>; label: string; low: string; high: string }

const PERSONALITY: Slider[] = [
  { key: 'talkativeness', label: t('Talkativeness'), low: t('Brief'), high: t('Chatty') },
  { key: 'warmth', label: t('Warmth'), low: t('Neutral'), high: t('Warm') },
  { key: 'formality', label: t('Formality'), low: t('Casual'), high: t('Formal') },
]
const VOICE: Slider[] = [
  { key: 'expressiveness', label: t('Expressiveness'), low: t('Calm'), high: t('Lively') },
  { key: 'pace', label: t('Pace'), low: t('Faster'), high: t('Slower, more careful') },
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
      .catch(() => !cancelled && setError(t('Could not load your settings.')))
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
      setError(t('Could not save your settings.'))
    } finally {
      setBusy(false)
    }
  }

  const preview = async () => {
    // Plays a fixed phrase in your voice with the values currently saved (there is no free text-to-speech, ADR 0013).
    setError(null)
    const blob = await api.ack(initialLanguage(voice.defaultLanguage) === 'en' ? 'en' : 'sv')
    if (!blob) return setError(t('Voice is unavailable right now.'))
    const url = URL.createObjectURL(blob)
    const audio = new Audio(url)
    audio.onended = () => URL.revokeObjectURL(url)
    void audio.play()
  }

  if (!draft || !settings) return <p className="muted">{error ?? t('Loading…')}</p>

  return (
    <div className="voicepage">
      <h1>{t('Voice and personality')}</h1>
      <GpuStatus api={api} />

      <section className="card">
        <h2>{t('Personality')}</h2>
        <p className="muted small">
          {t('How the agent talks to you, in text and when speaking. These are style preferences only: they never change what the agent is allowed to do.')}
        </p>
        {PERSONALITY.map((s) => (
          <SliderRow key={s.key} slider={s} value={draft[s.key]} onChange={(v) => setDraft({ ...draft, [s.key]: v })} />
        ))}
      </section>

      <section className="card">
        <h2>{t('Voice')}</h2>
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
          <p className="muted">{t('Voice is not configured on this installation.')}</p>
        )}
        <div className="row">
          <button type="button" className="btn primary" disabled={!dirty || busy} onClick={() => void save()}>
            {t('Save')}
          </button>
          {voice.enabled && (
            <button type="button" className="btn" disabled={dirty} onClick={() => void preview()} title={dirty ? t('Save first, then listen') : undefined}>
              {t('Listen')}
            </button>
          )}
          {saved && <span className="muted small" role="status">{t('Saved')}</span>}
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
          <h2>{t('Dictation vocabulary')}</h2>
          <VocabularyEditor api={api} />
        </section>
      )}

      {error && (
        <p role="alert" className="error small">
          {error}
        </p>
      )}
      <OwnVoicesAdmin api={api} />
    </div>
  )
}

interface OwnVoices {
  voices: { userId: string; seconds: number; consentAt: string; registrationsLast30Days: number }[]
  recentEvents: { userId: string; event: string; actor: string; at: string }[]
}

/** Admins (#93): who has a recorded voice, the consent trail, and revoking one. Hidden for everyone else. */
function OwnVoicesAdmin({ api }: { api: Api }) {
  const [data, setData] = useState<OwnVoices | null>(null)
  const [error, setError] = useState<string | null>(null)
  const load = useCallback(() => {
    api.raw<OwnVoices>('/admin/voices').then(setData).catch(() => setData(null))
  }, [api])
  useEffect(load, [load])
  if (!data) return null

  const revoke = async (user: string) => {
    if (!window.confirm(`Delete the recorded voice of ${user}? They can record it again.`)) return
    setError(null)
    try {
      await api.raw(`/admin/voices/${encodeURIComponent(user)}`, { method: 'DELETE' })
      load()
    } catch (e) {
      setError(String(e))
    }
  }

  return (
    <section className="card">
      <h2>{t('Recorded voices (admin)')}</h2>
      {data.voices.length === 0 ? (
        <p className="muted small">{t('Nobody has recorded their own voice.')}</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>{t('User')}</th>
              <th>{t('Length')}</th>
              <th>{t('Consent given')}</th>
              <th>{t('Recordings (30 days)')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.voices.map((v) => (
              <tr key={v.userId}>
                <td>{v.userId}</td>
                <td>{v.seconds.toFixed(1)} s</td>
                <td>{fmt.dateTime(v.consentAt)}</td>
                <td>{v.registrationsLast30Days}</td>
                <td>
                  <button type="button" className="btn small" onClick={() => void revoke(v.userId)}>
                    {t('Revoke')}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {data.recentEvents.length > 0 && (
        <details>
          <summary className="small">{t('Consent trail')}</summary>
          <ul className="small muted">
            {data.recentEvents.map((e, i) => (
              <li key={i}>
                {fmt.dateTime(e.at)} · {e.userId} · {e.event.toLowerCase()}
                {e.actor !== e.userId && ` by ${e.actor}`}
              </li>
            ))}
          </ul>
        </details>
      )}
      {error && <p className="error small">{error}</p>}
    </section>
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
  const [passageIndex, setPassageIndex] = useState(0)
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
    stream.current?.getTracks().forEach((it) => it.stop())
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
        const elapsed = (Date.now() - startedAt.current) / 1000
        setSeconds(elapsed)
        if (elapsed >= MAX_SECONDS) stop()
      }, 200)
    } catch {
      release()
      setError(t('Microphone access was blocked. Allow it in the browser to record your voice.'))
    }
  }

  const saveClip = async () => {
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
          ? t('That recording could not be used (too short, too quiet or unreadable). Record 10–30 seconds in a quiet room and try again.')
          : t('Could not save your voice. Try again.'),
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
      setError(t('Could not delete your voice. Try again.'))
    } finally {
      setBusy(false)
    }
  }

  const tooShort = seconds < MIN_SECONDS

  return (
    <section className="card">
      <h2>{t('Your own voice')}</h2>
      <p className="muted small">
        {t('Record yourself and the agent speaks with your voice when it talks to you. The recording is used only for your own conversations, is never shared with other users and you can delete it here at any time. Only record your own voice.')}
      </p>

      {current && phase === 'idle' && (
        <div className="row">
          <span className="chip">{t('Your voice is active · {s} s', { s: Math.round(current.seconds) })}</span>
          <button type="button" className="btn" disabled={busy} onClick={() => void remove()}>
            {t('Delete my voice')}
          </button>
        </div>
      )}

      {phase === 'idle' && (
        <div className="row">
          <select
            aria-label={t('Reading language')}
            value={language}
            onChange={(e) => {
              setLanguage(e.target.value as 'sv' | 'en')
              setPassageIndex(0)
            }}
          >
            <option value="sv">{t('Read in Swedish')}</option>
            <option value="en">{t('Read in English')}</option>
          </select>
          <button type="button" className="btn primary" onClick={() => void start()}>
            {current ? t('Record again') : t('Record my voice')}
          </button>
        </div>
      )}

      {phase === 'idle' && (
        <p className="muted small">
          {t('Not sure what to say? Pick a text below: each one is written to give the voice something different to learn from. Read it naturally, as if telling a person. Reading two different texts gives the best result.')}
        </p>
      )}

      <div className="passagepicker" role="radiogroup" aria-label={t('Suggested texts')}>
        {PASSAGES[language].map((p, i) => (
          <button
            key={p.id}
            type="button"
            role="radio"
            aria-checked={i === passageIndex}
            className={`passagechoice ${i === passageIndex ? 'on' : ''}`}
            disabled={phase === 'recording'}
            onClick={() => setPassageIndex(i)}
          >
            <span className="passagetitle">{p.title}</span>
            <span className="muted small">{p.covers}</span>
          </button>
        ))}
      </div>

      <blockquote className="script" aria-label={t('Text to read')}>
        {PASSAGES[language][passageIndex].text}
      </blockquote>

      {phase === 'recording' && (
        <div className="row">
          <div className="meter" aria-hidden="true">
            <div className="meterfill" style={{ width: `${Math.round(level * 100)}%` }} />
          </div>
          <span className="mono" role="timer">
            {seconds.toFixed(0)} / {MAX_SECONDS} s
          </span>
          <button type="button" className="btn primary" onClick={stop} disabled={tooShort} title={tooShort ? `Keep reading, at least ${MIN_SECONDS} s` : undefined}>
            {t('Stop')}
          </button>
        </div>
      )}

      {phase === 'review' && clipUrl && (
        <>
          <audio controls src={clipUrl} aria-label={t('Your recording')} />
          <label className="consent">
            <input type="checkbox" checked={consent} onChange={(e) => setConsent(e.target.checked)} />
            {t('This recording is my own voice. Lots may use it only to speak answers to me, and I can delete it at any time.')}
          </label>
          <div className="row">
            <button type="button" className="btn primary" disabled={!consent || busy} onClick={() => void saveClip()}>
              {t('Use this voice')}
            </button>
            <button type="button" className="btn" disabled={busy} onClick={() => setPhase('idle')}>
              {t('Discard')}
            </button>
          </div>
        </>
      )}
    </section>
  )
}

/** The voice GPU right now (#84): when it is busy or nearly full, answers use the fast voice for a while. */
function GpuStatus({ api }: { api: Api }) {
  const [s, setS] = useState<{ enabled: boolean; gpuLow: boolean; gpuFreeBytes: number | null; gpuTotalBytes: number | null; expressiveLoaded: boolean | null; expressiveWaiting: number } | null>(null)
  useEffect(() => {
    api.raw<NonNullable<typeof s>>('/voice/status').then(setS).catch(() => setS(null))
  }, [api])
  if (!s?.enabled || s.gpuFreeBytes == null) return null
  const gb = (b: number) => fmt.number(b / 1024 ** 3, 1)
  return (
    <p className={s.gpuLow ? 'warn small' : 'muted small'}>
      {t('Voice GPU: {free} of {total} GB free', { free: gb(s.gpuFreeBytes), total: gb(s.gpuTotalBytes ?? 0) })}
      {s.expressiveLoaded === false && ' · ' + t('expressive voice not loaded (answers start with the fast voice)')}
      {s.gpuLow && ' · ' + t('nearly full: answers may use the fast voice')}
      {s.expressiveWaiting > 0 && ` · ${t('{n} waiting', { n: s.expressiveWaiting })}`}
    </p>
  )
}
