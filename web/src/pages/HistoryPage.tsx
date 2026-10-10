import { useEffect, useState } from 'react'
import AudioClip from '../components/AudioClip'
import { ApiError, type Api, type ConversationDetail, type ConversationList, type ConversationSummary, type StageTotals, type TimelineEvent } from '../api'

const KINDS = [
  { kind: 'stt', label: 'Speech to text', key: 'sttMs' },
  { kind: 'llm', label: 'Model (LLM)', key: 'llmMs' },
  { kind: 'tool', label: 'Tools', key: 'toolMs' },
  { kind: 'tts', label: 'Text to speech', key: 'ttsMs' },
  { kind: 'other', label: 'Other (queue, network, orchestration)', key: 'otherMs' },
] as const

const fmt = (ms: number) => (ms < 1000 ? `${Math.round(ms)} ms` : ms < 60_000 ? `${(ms / 1000).toFixed(1)} s` : `${Math.floor(ms / 60_000)} min ${Math.round((ms % 60_000) / 1000)} s`)
const when = (iso: string) => new Date(iso).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })
const total = (s: StageTotals) => s.sttMs + s.llmMs + s.toolMs + s.ttsMs + s.otherMs

/** Where time went: one stacked bar with the share of every stage. */
function StageBar({ stages }: { stages: StageTotals }) {
  const sum = total(stages) || 1
  return (
    <div className="stagebar" role="img" aria-label="Time per stage">
      <div className="stagetrack">
        {KINDS.map((k) => (
          <div key={k.kind} className={`stageseg ${k.kind}`} style={{ width: `${(stages[k.key] / sum) * 100}%` }} title={`${k.label}: ${fmt(stages[k.key])}`} />
        ))}
      </div>
      <ul className="stagelegend">
        {KINDS.map((k) => (
          <li key={k.kind}>
            <span className={`dot ${k.kind}`} />
            {k.label} <strong>{fmt(stages[k.key])}</strong> <span className="muted small">{Math.round((stages[k.key] / sum) * 100)} %</span>
          </li>
        ))}
      </ul>
    </div>
  )
}

export default function HistoryPage({ api, id }: { api: Api; id?: string }) {
  return id ? <Detail api={api} id={id} /> : <List api={api} />
}

/** Per day: how many turns and where their time went (stacked bars). */
function DailyChart({ api }: { api: Api }) {
  const [days, setDays] = useState<Awaited<ReturnType<Api['conversationStats']>>>([])
  useEffect(() => {
    api.conversationStats(30).then(setDays).catch(() => setDays([]))
  }, [api])
  if (days.length < 2) return null
  const max = Math.max(...days.map((d) => d.sttMs + d.llmMs + d.toolMs + d.ttsMs), 1)
  return (
    <div className="card">
      <h2>Last 30 days: time per stage and day</h2>
      <div className="bars stacked" role="img" aria-label="Time per stage and day">
        {days.map((d) => (
          <div key={d.day} className="bar" title={`${d.day}: ${d.conversations} conversations, ${d.turns} turns, avg turn ${fmt(d.avgTurnMs)}`}>
            {(['tts', 'tool', 'llm', 'stt'] as const).map((k) => (
              <div key={k} className={`fill ${k}`} style={{ height: `${(d[`${k}Ms`] / max) * 100}%` }} />
            ))}
            <span className="small muted">{d.day.slice(5)}</span>
          </div>
        ))}
      </div>
    </div>
  )
}

function List({ api }: { api: Api }) {
  const [q, setQ] = useState('')
  const [status, setStatus] = useState('')
  const [profile, setProfile] = useState('')
  const [from, setFrom] = useState('')
  const [applied, setApplied] = useState({ q: '', status: '', profile: '', from: '' })
  const [data, setData] = useState<ConversationList | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    api
      .listConversations(applied)
      .then((d) => !cancelled && (setData(d), setError(null)))
      .catch(() => !cancelled && setError('Could not load the history.'))
    return () => {
      cancelled = true
    }
  }, [api, applied])

  return (
    <section>
      <h1>Conversation history</h1>
      <p className="muted small">Voice conversations are recorded (both sides) and the audio is kept for 30 days; you can delete it any time.</p>
      {data && data.count > 0 && (
        <div className="card">
          <h2>Where the time goes ({data.count} conversations)</h2>
          <StageBar stages={data.stages} />
        </div>
      )}
      <DailyChart api={api} />
      <form
        className="filters"
        onSubmit={(e) => {
          e.preventDefault()
          setApplied({ q, status, profile, from })
        }}
      >
        <label>
          Search
          <input className="wide" value={q} placeholder="Search what was said" onChange={(e) => setQ(e.target.value)} />
        </label>
        <label>
          Status
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">All</option>
            <option value="Completed">Completed</option>
            <option value="Failed">Failed</option>
            <option value="Running">Running</option>
          </select>
        </label>
        <label>
          Profile
          <input value={profile} onChange={(e) => setProfile(e.target.value)} />
        </label>
        <label>
          From
          <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <button className="btn" type="submit">
          Apply
        </button>
      </form>
      {error && <p className="error">{error}</p>}
      {data && data.count === 0 && <p className="muted">No conversations yet. Start one from the Runs page.</p>}
      {data && data.count > 0 && (
        <table>
          <thead>
            <tr>
              <th>Conversation</th>
              <th>User</th>
              <th>Date</th>
              <th>Duration</th>
              <th>Messages</th>
              <th>LLM share</th>
              <th>Tokens</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            {data.conversations.map((c) => (
              <ConversationRow key={c.id} c={c} />
            ))}
          </tbody>
        </table>
      )}
    </section>
  )
}

function ConversationRow({ c }: { c: ConversationSummary }) {
  const sum = total(c.stages) || 1
  return (
    <tr>
      <td>
        <a href={`#/history/${c.id}`}>{c.title}</a>
        <div className="muted small">{c.voice ? 'Voice' : 'Text'} · {c.profile}</div>
      </td>
      <td>{c.userId}</td>
      <td>{when(c.startedAt)}</td>
      <td>{fmt(c.durationMs)}</td>
      <td>{c.messages}</td>
      <td>{Math.round((c.stages.llmMs / sum) * 100)} %</td>
      <td className="mono">{c.promptTokens + c.completionTokens}</td>
      <td>
        <span className={`status ${c.status}`}>{c.status}</span>
      </td>
    </tr>
  )
}

function Detail({ api, id }: { api: Api; id: string }) {
  const [d, setD] = useState<ConversationDetail | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [audio, setAudio] = useState<Awaited<ReturnType<Api['conversationAudio']>>>([])
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    api.conversationAudio(id).then(setAudio).catch(() => setAudio([]))
  }, [api, id])

  useEffect(() => {
    let cancelled = false
    api
      .getConversation(id)
      .then((x) => !cancelled && setD(x))
      .catch((e) => !cancelled && setError(e instanceof ApiError && e.status === 404 ? 'Conversation not found.' : 'Could not load the conversation.'))
    return () => {
      cancelled = true
    }
  }, [api, id])

  if (error) return <p className="error">{error}</p>
  if (!d) return <p className="muted">Loading…</p>
  const c = d.conversation

  return (
    <section>
      <p>
        <a href="#/history">← All conversations</a>
      </p>
      <h1>{c.title}</h1>
      {c.summary ? <p className="summary">{c.summary}</p> : null}
      <p>
        <button type="button" className="btn small" disabled={busy} onClick={() => {
          setBusy(true)
          void api.summarizeConversation(id).then((s) => setD({ ...d, conversation: { ...c, title: s.title, summary: s.summary } })).finally(() => setBusy(false))
        }}>
          {c.summary ? 'Summarise again' : 'Summarise'}
        </button>{' '}
        {audio.length > 0 && (
          <button type="button" className="btn small" onClick={() => {
            if (window.confirm('Delete the recorded audio of this conversation?')) void api.deleteConversationAudio(id).then(() => setAudio([]))
          }}>
            Delete audio
          </button>
        )}{' '}
        <button type="button" className="btn small" onClick={() => {
          if (window.confirm('Delete this whole conversation (turns, trace and audio)? The audit log is kept.')) void api.deleteConversation(id).then(() => (window.location.hash = '#/history'))
        }}>
          Delete conversation
        </button>
      </p>
      <dl className="meta">
        <div><dt>Date</dt><dd>{when(c.startedAt)}</dd></div>
        <div><dt>Duration</dt><dd>{fmt(c.durationMs)}</dd></div>
        <div><dt>User</dt><dd>{c.userId}</dd></div>
        <div><dt>Profile</dt><dd>{c.profile}</dd></div>
        <div><dt>Status</dt><dd><span className={`status ${c.status}`}>{c.status}</span></dd></div>
        <div><dt>Conversation id</dt><dd className="mono small">{c.id}</dd></div>
        <div><dt>Tokens</dt><dd>{c.promptTokens} in · {c.completionTokens} out</dd></div>
      </dl>

      <div className="card">
        <h2>Where the time went</h2>
        <StageBar stages={c.stages} />
      </div>

      <h2>Timeline</h2>
      {d.turns.map((t, i) => (
        <div key={t.runId} className="turn card">
          <div className="turnhead">
            <strong>Turn {i + 1}</strong>
            <span className="muted small">{fmt(t.durationMs)} · <a href={`#/runs/${t.runId}`}>run details</a></span>
          </div>
          <p className="said you">
            <span className="who">You</span> {t.prompt}{' '}
            {audioFor(audio, d.turns, i, 'user').map((a) => <AudioClip key={a.id} api={api} id={a.id} label="your voice" />)}
          </p>
          <Timeline events={t.events} />
          <p className={`said agent ${t.status === 'Failed' ? 'failed' : ''}`}>
            <span className="who">Agent</span> {t.answer ?? t.error ?? '…'}{' '}
            {audio.filter((a) => a.kind === 'agent' && a.runId === t.runId).map((a) => <AudioClip key={a.id} api={api} id={a.id} label="spoken answer" />)}
          </p>
        </div>
      ))}
    </section>
  )
}

/** Recordings belong to the turn that started next after them (dictation happens before its run exists). */
function audioFor(audio: Awaited<ReturnType<Api['conversationAudio']>>, turns: ConversationDetail['turns'], i: number, kind: 'user') {
  const start = (n: number) => (n < turns.length ? new Date(turns[n].startedAt).getTime() : Infinity)
  return audio.filter((a) => a.kind === kind && new Date(a.at).getTime() <= start(i) + 1000 && new Date(a.at).getTime() > (i === 0 ? -Infinity : start(i - 1) + 1000))
}

function Timeline({ events }: { events: TimelineEvent[] }) {
  if (events.length === 0) return null
  const start = Math.min(...events.map((e) => e.startMs))
  const end = Math.max(...events.map((e) => e.startMs + e.durationMs))
  const span = Math.max(1, end - start)
  return (
    <ol className="timeline" aria-label="Timeline of this turn">
      {events.map((e, i) => (
        <li key={i}>
          <span className="tlabel">
            <span className={`dot ${e.kind}`} />
            {e.kind === 'llm' ? 'LLM' : e.kind === 'stt' ? 'STT' : e.kind === 'tts' ? 'TTS' : 'TOOL'} <span className="muted small">{e.name}</span>
          </span>
          <span className="ttrack">
            <span className={`tbar ${e.kind}`} style={{ left: `${((e.startMs - start) / span) * 100}%`, width: `${Math.max(0.8, (e.durationMs / span) * 100)}%` }} />
          </span>
          <span className="tdur mono">
            {fmt(e.durationMs)}
            {e.completionTokens != null && <span className="muted small"> · {e.promptTokens ?? 0}→{e.completionTokens} tok</span>}
          </span>
        </li>
      ))}
    </ol>
  )
}
