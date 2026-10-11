import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ApiError, type Api } from '../api'
import { fmt, t } from '../i18n'

/** Meetings (#41, #42): upload a recording, follow its processing, read the transcript with speakers, rename them, export. */

interface Meeting {
  id: string
  title: string
  status: 'queued' | 'processing' | 'done' | 'failed' | 'cancelled'
  error: string | null
  language: string | null
  durationSeconds: number | null
  speakerCount: number
  audioAvailable: boolean
  createdAt: string
  processedAt: string | null
  bytes: number
}
interface Line { seq: number; start: number; end: number; speaker: string; label: string; text: string }
interface Detail { meeting: Meeting; lines: Line[]; speakerNames: Record<string, string> }

const ACTIVE = ['queued', 'processing']
const clock = (s: number) => `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}`
const statusClass: Record<string, string> = { done: 'Allowed', failed: 'Denied', cancelled: 'Denied', queued: 'ApprovalRequested', processing: 'ApprovalRequested' }

export default function MeetingsPage({ api, id }: { api: Api; id?: string }) {
  return id ? <MeetingView api={api} id={id} /> : <MeetingList api={api} />
}

function MeetingList({ api }: { api: Api }) {
  const [list, setList] = useState<Meeting[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const load = useCallback(() => void api.raw<Meeting[]>('/meetings').then(setList, (e: unknown) => setError(String(e))), [api])
  useEffect(load, [load])
  // Poll only while something is still being processed.
  const busy = list?.some((m) => ACTIVE.includes(m.status)) ?? false
  useEffect(() => {
    if (!busy) return
    const timer = window.setInterval(load, 3000)
    return () => window.clearInterval(timer)
  }, [busy, load])

  return (
    <section>
      <h1>{t('Meetings')}</h1>
      <p className="muted small">{t('Upload a recording and get a transcript that says who said what. Speaker labels are suggestions: name them yourself.')}</p>
      <Upload api={api} onUploaded={load} />
      {error && <p className="error">{error}</p>}
      {list && list.length === 0 && <p className="muted">{t('No meetings yet.')}</p>}
      {list && list.length > 0 && (
        <table>
          <thead>
            <tr><th>{t('Meeting')}</th><th>{t('Status')}</th><th>{t('Length')}</th><th>{t('Speakers')}</th><th>{t('Uploaded')}</th></tr>
          </thead>
          <tbody>
            {list.map((m) => (
              <tr key={m.id}>
                <td><a href={`#/meetings/${m.id}`}>{m.title}</a></td>
                <td>
                  <span className={`decision ${statusClass[m.status]}`}>{t(m.status)}</span>
                  {m.error && <span className="error small"> {m.error}</span>}
                </td>
                <td className="mono">{m.durationSeconds ? clock(m.durationSeconds) : '–'}</td>
                <td>{m.status === 'done' ? m.speakerCount : '–'}</td>
                <td className="mono small">{fmt.dateTime(m.createdAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  )
}

function Upload({ api, onUploaded }: { api: Api; onUploaded: () => void }) {
  const [file, setFile] = useState<File | null>(null)
  const [title, setTitle] = useState('')
  const [language, setLanguage] = useState('')
  const [speakers, setSpeakers] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const submit = async () => {
    if (!file) return
    setBusy(true)
    setError(null)
    try {
      const form = new FormData()
      form.append('File', file)
      if (title.trim()) form.append('Title', title.trim())
      if (language) form.append('Language', language)
      if (speakers) form.append('Speakers', speakers)
      const res = await fetch('/meetings', { method: 'POST', body: form, headers: await api.authHeaders() })
      if (!res.ok) throw new ApiError(res.status, await res.text())
      setFile(null)
      setTitle('')
      onUploaded()
    } catch (e) {
      setError(e instanceof ApiError && e.status === 503 ? t('Meetings are not available on this installation.') : String(e))
    } finally {
      setBusy(false)
    }
  }
  return (
    <form className="filters card" onSubmit={(e) => { e.preventDefault(); void submit() }}>
      <label>
        {t('Recording')}
        <input type="file" accept="audio/*,video/*" onChange={(e) => setFile(e.target.files?.[0] ?? null)} required />
      </label>
      <label>
        {t('Title')}
        <input value={title} onChange={(e) => setTitle(e.target.value)} placeholder={t('e.g. Weekly operations')} />
      </label>
      <label>
        {t('Language')}
        <select value={language} onChange={(e) => setLanguage(e.target.value)}>
          <option value="">{t('Detect')}</option>
          <option value="sv">Svenska</option>
          <option value="en">English</option>
        </select>
      </label>
      <label>
        {t('Speakers (if known)')}
        <input type="number" min={1} max={20} value={speakers} onChange={(e) => setSpeakers(e.target.value)} />
      </label>
      <button className="btn primary" type="submit" disabled={!file || busy}>{busy ? t('Uploading…') : t('Upload')}</button>
      {error && <p className="error small">{error}</p>}
    </form>
  )
}

function MeetingView({ api, id }: { api: Api; id: string }) {
  const [detail, setDetail] = useState<Detail | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const [names, setNames] = useState<Record<string, string>>({})
  const [audioUrl, setAudioUrl] = useState<string | null>(null)
  const player = useRef<HTMLAudioElement | null>(null)
  const load = useCallback(() => void api.raw<Detail>(`/meetings/${id}`).then((d) => { setDetail(d); setNames(d.speakerNames) }, (e: unknown) => setError(String(e))), [api, id])
  useEffect(load, [load])
  const processing = detail ? ACTIVE.includes(detail.meeting.status) : false
  useEffect(() => {
    if (!processing) return
    const timer = window.setInterval(load, 3000)
    return () => window.clearInterval(timer)
  }, [processing, load])
  useEffect(() => () => { if (audioUrl) URL.revokeObjectURL(audioUrl) }, [audioUrl])

  const speakers = useMemo(() => [...new Set(detail?.lines.map((l) => l.speaker) ?? [])], [detail])
  const shown = useMemo(() => {
    const q = query.trim().toLowerCase()
    return (detail?.lines ?? []).filter((l) => !q || l.text.toLowerCase().includes(q) || l.label.toLowerCase().includes(q))
  }, [detail, query])

  const playFrom = async (seconds: number) => {
    let url = audioUrl
    if (!url) {
      const res = await fetch(`/meetings/${id}/audio`, { headers: await api.authHeaders() })
      if (!res.ok) return setError(t('The recording is no longer stored.'))
      url = URL.createObjectURL(await res.blob())
      setAudioUrl(url)
    }
    window.setTimeout(() => {
      if (!player.current) return
      player.current.currentTime = seconds
      void player.current.play()
    }, 50)
  }
  const download = async (format: string) => {
    const res = await fetch(`/meetings/${id}/export?format=${format}`, { headers: await api.authHeaders() })
    const a = document.createElement('a')
    a.href = URL.createObjectURL(await res.blob())
    a.download = `${detail?.meeting.title ?? 'meeting'}.${format}`
    a.click()
    URL.revokeObjectURL(a.href)
  }
  const remove = async (audioOnly: boolean) => {
    if (!window.confirm(audioOnly ? t('Delete the recording? The transcript stays.') : t('Delete this meeting, its recording and transcript?'))) return
    await api.raw(`/meetings/${id}${audioOnly ? '?audioOnly=true' : ''}`, { method: 'DELETE' })
    if (audioOnly) load()
    else window.location.hash = '#/meetings'
  }

  if (error) return <p className="error">{error}</p>
  if (!detail) return <p className="muted">{t('Loading…')}</p>
  const m = detail.meeting
  return (
    <section>
      <p><a href="#/meetings">← {t('Meetings')}</a></p>
      <h1>{m.title}</h1>
      <p className="small muted">
        <span className={`decision ${statusClass[m.status]}`}>{t(m.status)}</span> · {m.language ?? '–'} · {m.durationSeconds ? clock(m.durationSeconds) : '–'} ·{' '}
        {fmt.dateTime(m.createdAt)}
        {m.error && <span className="error"> · {m.error}</span>}
      </p>
      {ACTIVE.includes(m.status) && (
        <p>
          {t('Transcribing and telling the speakers apart…')}{' '}
          <button type="button" className="btn small" onClick={() => void api.raw(`/meetings/${id}/cancel`, { method: 'POST', body: '{}' }).then(load)}>{t('Cancel')}</button>
        </p>
      )}
      {m.status === 'done' && (
        <>
          <div className="card">
            <h2 className="section-title">{t('Speakers')}</h2>
            <p className="small muted">{t('The labels are the system’s guess. Give them names; exports use them.')}</p>
            <div className="filters">
              {speakers.map((s) => (
                <label key={s}>
                  {s}
                  <input value={names[s] ?? ''} placeholder={s} onChange={(e) => setNames({ ...names, [s]: e.target.value })} />
                </label>
              ))}
              <button type="button" className="btn" onClick={() => void api.raw(`/meetings/${id}/speakers`, { method: 'PUT', body: JSON.stringify({ names }) }).then(load)}>
                {t('Save names')}
              </button>
            </div>
          </div>
          <div className="row">
            <input className="wide" aria-label={t('Search the transcript')} placeholder={t('Search the transcript')} value={query} onChange={(e) => setQuery(e.target.value)} />
            {['txt', 'srt', 'vtt', 'json'].map((f) => (
              <button key={f} type="button" className="btn small" onClick={() => void download(f)}>{f.toUpperCase()}</button>
            ))}
          </div>
          {audioUrl && <audio ref={player} src={audioUrl} controls className="meeting-player" />}
          <ol className="meeting-lines">
            {shown.map((l) => (
              <li key={l.seq}>
                <button type="button" className="btn ghost small mono" onClick={() => void playFrom(l.start)} disabled={!m.audioAvailable}
                  title={m.audioAvailable ? t('Play from here') : t('The recording is no longer stored.')}>
                  {clock(l.start)}
                </button>
                <strong className={`speaker s${speakers.indexOf(l.speaker) % 6}`}>{l.label}</strong>
                <span>{l.text}</span>
              </li>
            ))}
          </ol>
        </>
      )}
      <p className="row">
        {m.audioAvailable && <button type="button" className="btn small" onClick={() => void remove(true)}>{t('Delete the recording')}</button>}
        <button type="button" className="btn small" onClick={() => void remove(false)}>{t('Delete the meeting')}</button>
      </p>
    </section>
  )
}
