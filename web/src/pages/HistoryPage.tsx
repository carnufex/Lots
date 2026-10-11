import { useEffect, useState } from 'react'
import AudioClip from '../components/AudioClip'
import { ApiError, type Api, type ChatGroup, type ConversationDetail, type ConversationList, type ConversationSummary, type StageTotals, type TimelineEvent } from '../api'
import { t } from '../i18n'
import { RunsList } from './RunsPage'

const KINDS = [
  { kind: 'stt', label: t('Speech to text'), key: 'sttMs' },
  { kind: 'llm', label: t('Model (LLM)'), key: 'llmMs' },
  { kind: 'tool', label: t('Tools'), key: 'toolMs' },
  { kind: 'tts', label: t('Text to speech'), key: 'ttsMs' },
  { kind: 'other', label: t('Other (queue, network, orchestration)'), key: 'otherMs' },
] as const

const fmt = (ms: number) => (ms < 1000 ? `${Math.round(ms)} ms` : ms < 60_000 ? `${(ms / 1000).toFixed(1)} s` : `${Math.floor(ms / 60_000)} min ${Math.round((ms % 60_000) / 1000)} s`)
const when = (iso: string) => new Date(iso).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })
const total = (s: StageTotals) => s.sttMs + s.llmMs + s.toolMs + s.ttsMs + s.otherMs

/** Where time went: one stacked bar with the share of every stage. */
function StageBar({ stages }: { stages: StageTotals }) {
  const sum = total(stages) || 1
  return (
    <div className="stagebar" role="img" aria-label={t('Time per stage')}>
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

export const HISTORY_TABS = [
  { slug: 'conversations', label: t('Conversations') },
  { slug: 'timing', label: t('Timing') },
  { slug: 'runs', label: t('Runs') },
] as const

export type HistoryTab = (typeof HISTORY_TABS)[number]['slug']

/** History (#152): past conversations, where their time went, and every run (scheduled and API runs included). */
export default function HistoryPage({ api, id, tab = 'conversations' }: { api: Api; id?: string; tab?: HistoryTab }) {
  if (id) return <Detail api={api} id={id} />
  return (
    <section>
      <h1>{t('History')}</h1>
      <div className="tabs" role="tablist" aria-label={t('History')}>
        {HISTORY_TABS.map((it) => (
          <a key={it.slug} href={it.slug === 'runs' ? '#/runs' : `#/history/${it.slug}`} role="tab" aria-selected={it.slug === tab} className={it.slug === tab ? 'tab active' : 'tab'}>
            {it.label}
          </a>
        ))}
      </div>
      {tab === 'conversations' && <List api={api} />}
      {tab === 'timing' && <Timing api={api} />}
      {tab === 'runs' && <RunsList api={api} />}
    </section>
  )
}

/** Where the time goes across the caller's conversations (#46), per stage and per day. */
function Timing({ api }: { api: Api }) {
  const [data, setData] = useState<ConversationList | null>(null)
  useEffect(() => {
    api.listConversations({ q: '', status: '', profile: '', from: '' }).then(setData).catch(() => setData(null))
  }, [api])
  return (
    <>
      {data && data.count > 0 ? (
        <div className="card">
          <h2>{t('Where the time goes ({n} conversations)', { n: data.count })}</h2>
          <StageBar stages={data.stages} />
        </div>
      ) : (
        <p className="muted">{t('No conversations yet.')}</p>
      )}
      <DailyChart api={api} />
    </>
  )
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
      <h2>{t('Last 30 days: time per stage and day')}</h2>
      <div className="bars stacked" role="img" aria-label={t('Time per stage and day')}>
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
  const [group, setGroup] = useState('')
  const [groups, setGroups] = useState<ChatGroup[]>([])
  useEffect(() => void api.groups().then(setGroups, () => setGroups([])), [api])
  const [applied, setApplied] = useState({ q: '', status: '', profile: '', from: '', group: '' })
  const [data, setData] = useState<ConversationList | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    api
      .listConversations(applied)
      .then((d) => !cancelled && (setData(d), setError(null)))
      .catch(() => !cancelled && setError(t('Could not load the history.')))
    return () => {
      cancelled = true
    }
  }, [api, applied])

  return (
    <>
      <p className="muted small">{t('Voice conversations are recorded (both sides) and the audio is kept for 30 days; you can delete it any time.')}</p>
      <form
        className="filters"
        onSubmit={(e) => {
          e.preventDefault()
          setApplied({ q, status, profile, from, group })
        }}
      >
        <label>
          {t('Search')}
          <input className="wide" value={q} placeholder={t('Search what was said')} onChange={(e) => setQ(e.target.value)} />
        </label>
        <label>
          {t('Status')}
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">{t('All')}</option>
            <option value="Completed">{t('Completed')}</option>
            <option value="Failed">{t('Failed')}</option>
            <option value="Running">{t('Running')}</option>
          </select>
        </label>
        <label>
          {t('Context')}
          <input value={profile} onChange={(e) => setProfile(e.target.value)} />
        </label>
        {groups.length > 0 && (
          <label>
            {t('Group')}
            <select value={group} onChange={(e) => setGroup(e.target.value)}>
              <option value="">{t('All')}</option>
              <option value="none">{t('No group')}</option>
              {groups.map((g) => (
                <option key={g.id} value={g.id}>
                  {g.name}
                </option>
              ))}
            </select>
          </label>
        )}
        <label>
          {t('From')}
          <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <button className="btn" type="submit">
          {t('Apply')}
        </button>
      </form>
      {error && <p className="error">{error}</p>}
      {data && data.count === 0 && <p className="muted">{t('No conversations yet.')} <a href="#/chat">{t('Start a chat')}</a></p>}
      {data && data.count > 0 && (
        <table>
          <thead>
            <tr>
              <th>{t('Conversation')}</th>
              <th>{t('User')}</th>
              <th>{t('Date')}</th>
              <th>{t('Duration')}</th>
              <th>{t('Messages')}</th>
              <th>{t('LLM share')}</th>
              <th>{t('Tokens')}</th>
              <th>{t('Status')}</th>
            </tr>
          </thead>
          <tbody>
            {data.conversations.map((c) => (
              <ConversationRow key={c.id} c={c} />
            ))}
          </tbody>
        </table>
      )}
    </>
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
      .catch((e) => !cancelled && setError(e instanceof ApiError && e.status === 404 ? t('Conversation not found.') : t('Could not load the conversation.')))
    return () => {
      cancelled = true
    }
  }, [api, id])

  if (error) return <p className="error">{error}</p>
  if (!d) return <p className="muted">{t('Loading…')}</p>
  const c = d.conversation

  return (
    <section>
      <p>
        <a href="#/history">{t('← All conversations')}</a>
      </p>
      <h1>{c.title}</h1>
      {c.summary ? <p className="summary">{c.summary}</p> : null}
      <p>
        <button type="button" className="btn small" disabled={busy} onClick={() => {
          setBusy(true)
          void api.summarizeConversation(id).then((s) => setD({ ...d, conversation: { ...c, title: s.title, summary: s.summary } })).finally(() => setBusy(false))
        }}>
          {c.summary ? t('Summarise again') : 'Summarise'}
        </button>{' '}
        {audio.length > 0 && (
          <button type="button" className="btn small" onClick={() => {
            if (window.confirm(t('Delete the recorded audio of this conversation?'))) void api.deleteConversationAudio(id).then(() => setAudio([]))
          }}>
            {t('Delete audio')}
          </button>
        )}{' '}
        <button type="button" className="btn small" onClick={() => {
          if (window.confirm(t('Delete this whole conversation (turns, trace and audio)? The audit log is kept.'))) void api.deleteConversation(id).then(() => (window.location.hash = '#/history'))
        }}>
          {t('Delete conversation')}
        </button>
      </p>
      <dl className="meta">
        <div><dt>{t('Date')}</dt><dd>{when(c.startedAt)}</dd></div>
        <div><dt>{t('Duration')}</dt><dd>{fmt(c.durationMs)}</dd></div>
        <div><dt>{t('User')}</dt><dd>{c.userId}</dd></div>
        <div><dt>{t('Context')}</dt><dd>{c.profile}</dd></div>
        <div><dt>{t('Status')}</dt><dd><span className={`status ${c.status}`}>{c.status}</span></dd></div>
        <div><dt>{t('Conversation id')}</dt><dd className="mono small">{c.id}</dd></div>
        <div><dt>{t('Tokens')}</dt><dd>{c.promptTokens} in · {c.completionTokens} out</dd></div>
      </dl>

      <div className="card">
        <h2>{t('Where the time went')}</h2>
        <StageBar stages={c.stages} />
      </div>

      <h2>{t('Timeline')}</h2>
      {d.turns.map((it, i) => (
        <div key={it.runId} className="turn card">
          <div className="turnhead">
            <strong>{t('Turn {n}', { n: i + 1 })}</strong>
            <span className="muted small">{fmt(it.durationMs)} · <a href={`#/runs/${it.runId}`}>{t('run details')}</a></span>
          </div>
          <p className="said you">
            <span className="who">{t('You')}</span> {it.prompt}{' '}
            {audioFor(audio, d.turns, i, 'user').map((a) => <AudioClip key={a.id} api={api} id={a.id} label="your voice" />)}
          </p>
          <Timeline events={it.events} />
          <p className={`said agent ${it.status === 'Failed' ? 'failed' : ''}`}>
            <span className="who">{t('Agent')}</span> {it.answer ?? it.error ?? '…'}{' '}
            {audio.filter((a) => a.kind === 'agent' && a.runId === it.runId).map((a) => <AudioClip key={a.id} api={api} id={a.id} label="spoken answer" />)}
          </p>
          {it.rating != null && (
            <p className="small muted">
              {it.rating > 0 ? '👍 ' + t('You rated this answer good') : '👎 ' + t('You rated this answer bad')}
              {it.feedbackComment && <>: <q>{it.feedbackComment}</q></>}
            </p>
          )}
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
    <ol className="timeline" aria-label={t('Timeline of this turn')}>
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
