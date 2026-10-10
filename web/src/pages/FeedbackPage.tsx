import { useEffect, useState } from 'react'
import { ApiError, type Api, type FeedbackItem } from '../api'
import Markdown from '../components/Markdown'
import { fmt, t } from '../i18n'

const list = (s: string) => s.split(',').map((x) => x.trim()).filter(Boolean)

/** The review queue (#121): ratings with their question and answer; resolve them or turn a bad answer into an eval case. */
export default function FeedbackPage({ api }: { api: Api }) {
  const [state, setState] = useState('Open')
  const [rating, setRating] = useState('down')
  const [data, setData] = useState<Awaited<ReturnType<Api['feedbackQueue']>> | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [forbidden, setForbidden] = useState(false)
  const [reload, setReload] = useState(0)

  useEffect(() => {
    let cancelled = false
    api.feedbackQueue({ state, rating }).then((d) => {
      if (!cancelled) { setData(d); setError(null) }
    }).catch((e) => {
      if (cancelled) return
      if (e instanceof ApiError && e.status === 403) setForbidden(true)
      else setError(e instanceof Error ? e.message : String(e))
    })
    return () => {
      cancelled = true
    }
  }, [api, state, rating, reload])

  const download = async (kind: 'eval-cases' | 'labels') => {
    const body = await api.feedbackExport(kind)
    const url = URL.createObjectURL(new Blob([JSON.stringify(body, null, 2) + '\n'], { type: 'application/json' }))
    const a = document.createElement('a')
    a.href = url
    a.download = kind === 'labels' ? 'feedback-labels.json' : 'feedback.json'
    a.click()
    URL.revokeObjectURL(url)
  }

  if (forbidden)
    return (
      <section>
        <h1>{t('Feedback')}</h1>
        <p>{t('The review queue is only available to reviewers (Feedback:ReviewRoles, default admin).')}</p>
      </section>
    )

  return (
    <section>
      <h1>{t('Feedback')}</h1>
      <p className="muted summary">
        {t('Ratings users gave answers. Turn a bad answer into an eval case so it is tested from now on; the dataset and the judge labels download as files for Lots.Evals.')}
      </p>
      <div className="filters">
        <label>
          {t('State')}
          <select value={state} onChange={(e) => setState(e.target.value)}>
            <option value="Open">{t('Open')}</option>
            <option value="Resolved">{t('Resolved')}</option>
            <option value="Converted">{t('Eval case')}</option>
            <option value="">{t('All')}</option>
          </select>
        </label>
        <label>
          {t('Rating')}
          <select value={rating} onChange={(e) => setRating(e.target.value)}>
            <option value="down">👎 {t('Bad')}</option>
            <option value="up">👍 {t('Good')}</option>
            <option value="">{t('All')}</option>
          </select>
        </label>
        <span className="grow" />
        <button type="button" className="btn" onClick={() => void download('eval-cases')}>{t('Download eval cases')}</button>
        <button type="button" className="btn" onClick={() => void download('labels')}>{t('Download judge labels')}</button>
      </div>
      {data && <p className="muted small">{t('{open} open: {down} bad, {up} good', { open: data.open, down: data.down, up: data.up })}</p>}
      {error && <p role="alert" className="error">{t('Could not load feedback:')} {error}</p>}
      {data && data.items.length === 0 && <p className="muted">{t('Nothing here.')}</p>}
      <ul className="feedback-list">
        {data?.items.map((it) => <Item key={it.id} api={api} item={it} onDone={() => setReload((n) => n + 1)} />)}
      </ul>
    </section>
  )
}

function Item({ api, item, onDone }: { api: Api; item: FeedbackItem; onDone: () => void }) {
  const [mode, setMode] = useState<'view' | 'case' | 'resolve'>('view')
  const [note, setNote] = useState('')
  const [question, setQuestion] = useState(item.prompt)
  const [tools, setTools] = useState('')
  const [facts, setFacts] = useState('')
  const [forbid, setForbid] = useState('')
  const [refusal, setRefusal] = useState(false)
  const [judge, setJudge] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const act = async (f: () => Promise<unknown>) => {
    setBusy(true)
    setError(null)
    try {
      await f()
      onDone()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <li className="card">
      <p className="small muted">
        {item.rating > 0 ? '👍' : '👎'} <strong>{item.user}</strong> · {item.profile} · {fmt.dateTime(item.createdAt)} ·{' '}
        <a href={`#/runs/${item.runId}`}>{t('View run')}</a>
        {item.state !== 'Open' && <> · {item.state === 'Converted' ? t('eval case {id}', { id: item.case?.id ?? '' }) : t('resolved by {who}', { who: item.reviewedBy ?? '' })}</>}
      </p>
      <p><strong>{item.prompt}</strong></p>
      {item.answer && <div className="md feedback-answer"><Markdown text={item.answer} /></div>}
      {item.toolsCalled.length > 0 && <p className="small muted">{t('Tools called:')} <span className="mono">{item.toolsCalled.join(', ')}</span></p>}
      {item.comment && <blockquote className="feedback-comment">{item.comment}</blockquote>}
      {item.reviewNote && <p className="small muted">{t('Review note:')} {item.reviewNote}</p>}

      {mode === 'view' && item.state !== 'Converted' && (
        <div className="row">
          <button type="button" className="btn primary" onClick={() => setMode('case')}>{t('Make eval case')}</button>
          {item.state === 'Open' && <button type="button" className="btn" onClick={() => setMode('resolve')}>{t('Resolve')}</button>}
        </div>
      )}
      {mode === 'resolve' && (
        <form className="row" onSubmit={(e) => { e.preventDefault(); void act(() => api.resolveFeedback(item.id, note)) }}>
          <input className="wide" aria-label={t('Note')} placeholder={t('Note (optional), e.g. what was changed')} value={note} onChange={(e) => setNote(e.target.value)} />
          <button type="submit" className="btn primary" disabled={busy}>{t('Resolve')}</button>
          <button type="button" className="btn ghost" onClick={() => setMode('view')}>{t('Cancel')}</button>
        </form>
      )}
      {mode === 'case' && (
        <form className="casebox" onSubmit={(e) => {
          e.preventDefault()
          void act(() => api.feedbackToCase(item.id, {
            question, expectedTools: list(tools), expectedFacts: list(facts), forbiddenTools: list(forbid), expectRefusal: refusal, judge: judge.trim() || undefined,
          }))
        }}>
          <label>
            {t('Question (remove personal data: it goes into a dataset file)')}
            <textarea rows={2} value={question} onChange={(e) => setQuestion(e.target.value)} />
          </label>
          <label>
            {t('Expected tools (comma separated)')}
            <input value={tools} onChange={(e) => setTools(e.target.value)} placeholder={item.toolsCalled.join(', ')} />
          </label>
          <label>
            {t('Facts the answer must contain')}
            <input value={facts} onChange={(e) => setFacts(e.target.value)} />
          </label>
          <label>
            {t('Tools it must not call')}
            <input value={forbid} onChange={(e) => setForbid(e.target.value)} />
          </label>
          <label className="inline">
            <input type="checkbox" checked={refusal} onChange={(e) => setRefusal(e.target.checked)} /> {t('It should refuse (policy does not allow it)')}
          </label>
          <label>
            {t('What a correct answer does (judge criteria)')}
            <textarea rows={2} value={judge} onChange={(e) => setJudge(e.target.value)} />
          </label>
          <div className="row">
            <button type="submit" className="btn primary" disabled={busy}>{t('Save eval case')}</button>
            <button type="button" className="btn ghost" onClick={() => setMode('view')}>{t('Cancel')}</button>
          </div>
        </form>
      )}
      {error && <p role="alert" className="error">{error}</p>}
    </li>
  )
}
