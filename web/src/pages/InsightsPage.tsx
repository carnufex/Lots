import { useEffect, useState } from 'react'
import { ApiError, type Api, type Candidate, type FollowUp, type MinedCase, type OutcomeGroup, type OutcomeReport, type Proposal } from '../api'
import { fmt, t } from '../i18n'

type Tab = 'overview' | 'failures' | 'proposals'

const pct = (n: number) => `${Math.round(n * 100)} %`
const ms = (n: number) => (n < 1000 ? `${n} ms` : `${(n / 1000).toFixed(1)} s`)
const list = (s: string) => s.split(',').map((x) => x.trim()).filter(Boolean)

/**
 * Insights (#146): how Lots has been doing (run outcomes), failures mined into eval cases to review, and improvement proposals with
 * their eval delta, pull request and follow-up. The same view the self-improve agent gets, for people.
 */
export default function InsightsPage({ api, traceUrl }: { api: Api; traceUrl: string | null }) {
  const [tab, setTab] = useState<Tab>('overview')
  const [forbidden, setForbidden] = useState(false)

  if (forbidden)
    return (
      <section>
        <h1>{t('Insights')}</h1>
        <p>{t('Insights are available to admins, auditors and the self-improve role (Insights:Roles).')}</p>
      </section>
    )

  return (
    <section>
      <h1>{t('Insights')}</h1>
      <div className="tabs" role="tablist">
        {(['overview', 'failures', 'proposals'] as Tab[]).map((x) => (
          <button key={x} role="tab" type="button" aria-selected={tab === x} className={`tab ${tab === x ? 'active' : ''}`} onClick={() => setTab(x)}>
            {x === 'overview' ? t('Overview') : x === 'failures' ? t('Failures') : t('Proposals')}
          </button>
        ))}
      </div>
      {tab === 'overview' && <Overview api={api} traceUrl={traceUrl} onForbidden={() => setForbidden(true)} />}
      {tab === 'failures' && <Failures api={api} onForbidden={() => setForbidden(true)} />}
      {tab === 'proposals' && <Proposals api={api} onForbidden={() => setForbidden(true)} />}
    </section>
  )
}

function useLoad<T>(load: () => Promise<T>, deps: unknown[], onForbidden: () => void) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [n, setN] = useState(0)
  useEffect(() => {
    let cancelled = false
    load().then((d) => { if (!cancelled) { setData(d); setError(null) } }).catch((e) => {
      if (cancelled) return
      if (e instanceof ApiError && e.status === 403) onForbidden()
      else setError(e instanceof Error ? e.message : String(e))
    })
    return () => { cancelled = true }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, n])
  return { data, error, reload: () => setN((x) => x + 1) }
}

function Overview({ api, traceUrl, onForbidden }: { api: Api; traceUrl: string | null; onForbidden: () => void }) {
  const [days, setDays] = useState(7)
  const { data, error } = useLoad<OutcomeReport>(() => api.outcomes(days), [days], onForbidden)
  return (
    <>
      <div className="filters">
        <label>
          {t('Period')}
          <select value={days} onChange={(e) => setDays(Number(e.target.value))}>
            <option value={1}>{t('Last day')}</option>
            <option value={7}>{t('Last 7 days')}</option>
            <option value={30}>{t('Last 30 days')}</option>
          </select>
        </label>
      </div>
      {error && <p role="alert" className="error">{error}</p>}
      {!data && !error && <p className="muted">{t('Loading…')}</p>}
      {data && data.total.runs === 0 && <p className="muted">{t('No finished runs in this period.')}</p>}
      {data && data.total.runs > 0 && (
        <>
          <div className="stats">
            <Stat label={t('Runs')} value={fmt.number(data.total.runs)} />
            <Stat label={t('Success')} value={pct(data.total.successRate)} />
            <Stat label={t('p95 time')} value={ms(data.total.p95WallMs)} />
            <Stat label={t('Tool errors')} value={fmt.number(data.total.toolErrors)} />
            <Stat label={t('Rated bad')} value={fmt.number(data.total.thumbsDown)} />
            <Stat label={t('Cost')} value={data.total.cost.toFixed(2)} />
          </div>
          {data.byDay && data.byDay.length > 1 && (
            <div className="card">
              <h2>{t('Success rate per day')}</h2>
              <div className="bars" role="img" aria-label={t('Success rate per day')}>
                {data.byDay.map((d) => (
                  <div key={d.key} className="bar" title={`${d.key}: ${pct(d.successRate)} of ${d.runs}`}>
                    <div className="fill" style={{ height: `${Math.max(2, d.successRate * 100)}%` }} />
                  </div>
                ))}
              </div>
            </div>
          )}
          <Groups title={t('Per profile version')} rows={data.byProfileVersion} />
          <Groups title={t('Per model')} rows={data.byModel} />
          <Groups title={t('Per channel')} rows={data.byChannel} />
          {data.byTool.length > 0 && (
            <div className="card">
              <h2>{t('Per tool')}</h2>
              <table>
                <thead><tr><th>{t('Tool')}</th><th>{t('Calls')}</th><th>{t('Errors')}</th><th>{t('Error rate')}</th></tr></thead>
                <tbody>
                  {data.byTool.map((x) => (
                    <tr key={x.tool}><td className="mono">{x.tool}</td><td>{x.calls}</td><td>{x.errors}</td><td>{pct(x.errorRate)}</td></tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <div className="card">
            <h2>{t('Latest runs with a problem')}</h2>
            <ul className="plain">
              {data.items.filter((i) => i.problem !== 'none').slice(0, 15).map((i) => (
                <li key={i.runId}>
                  <a href={`#/runs/${i.runId}`}>{fmt.dateTime(i.endedAt)}</a> · {i.profile} v{i.profileVersion} · {i.channel} · <strong>{i.problem}</strong> · {ms(i.wallMs)}
                  {traceUrl && i.traceId && <> · <a href={traceUrl.replace('{traceId}', i.traceId)} target="_blank" rel="noreferrer noopener">{t('trace')}</a></>}
                </li>
              ))}
            </ul>
          </div>
        </>
      )}
    </>
  )
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="stat">
      <span className="muted small">{label}</span>
      <strong>{value}</strong>
    </div>
  )
}

function Groups({ title, rows }: { title: string; rows: OutcomeGroup[] }) {
  if (rows.length === 0) return null
  return (
    <div className="card">
      <h2>{title}</h2>
      <table>
        <thead><tr><th></th><th>{t('Runs')}</th><th>{t('Success')}</th><th>p50</th><th>p95</th><th>{t('Main problems')}</th></tr></thead>
        <tbody>
          {rows.map((g) => (
            <tr key={g.key}>
              <td className="mono">{g.key}</td><td>{g.runs}</td><td>{pct(g.successRate)}</td><td>{ms(g.p50WallMs)}</td><td>{ms(g.p95WallMs)}</td>
              <td className="small">{Object.entries(g.problems).sort((a, b) => b[1] - a[1]).slice(0, 3).map(([k, v]) => `${k} ${v}`).join(', ')}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function Failures({ api, onForbidden }: { api: Api; onForbidden: () => void }) {
  const [state, setState] = useState('Open')
  const [note, setNote] = useState<string | null>(null)
  const { data, error, reload } = useLoad<Candidate[]>(() => api.candidates(state), [state], onForbidden)

  const mine = async () => {
    try {
      const s = await api.mine()
      setNote(t('{runs} runs with signals, {clusters} clusters, {created} new', { runs: s.runsWithSignals, clusters: s.clusters, created: s.newCandidates }))
      reload()
    } catch (e) {
      setNote(e instanceof Error ? e.message : String(e))
    }
  }
  const download = async () => {
    const body = await api.minedDataset()
    const url = URL.createObjectURL(new Blob([JSON.stringify(body, null, 2) + '\n'], { type: 'application/json' }))
    const a = document.createElement('a')
    a.href = url
    a.download = 'mined.json'
    a.click()
    URL.revokeObjectURL(url)
  }

  return (
    <>
      <p className="muted summary">{t('Runs that went wrong the same way, clustered, each with a drafted eval case. Accepted cases join the mined dataset and the regression gate; nothing joins without a person.')}</p>
      <div className="filters">
        <label>
          {t('State')}
          <select value={state} onChange={(e) => setState(e.target.value)}>
            <option value="Open">{t('Open')}</option>
            <option value="Accepted">{t('Accepted')}</option>
            <option value="Rejected">{t('Rejected')}</option>
          </select>
        </label>
        <span className="grow" />
        <button type="button" className="btn" onClick={() => void mine()}>{t('Mine now')}</button>
        <button type="button" className="btn" onClick={() => void download()}>{t('Download accepted cases')}</button>
      </div>
      {note && <p className="muted small">{note}</p>}
      {error && <p role="alert" className="error">{error}</p>}
      {data && data.length === 0 && <p className="muted">{t('Nothing here.')}</p>}
      <ul className="feedback-list">
        {data?.map((c) => <CandidateItem key={c.id} api={api} c={c} onDone={reload} />)}
      </ul>
    </>
  )
}

function CandidateItem({ api, c, onDone }: { api: Api; c: Candidate; onDone: () => void }) {
  const shown = c.case ?? c.draft
  const [edit, setEdit] = useState(false)
  const [question, setQuestion] = useState(shown.question)
  const [tools, setTools] = useState((shown.expectedTools ?? []).join(', '))
  const [forbid, setForbid] = useState((shown.forbiddenTools ?? []).join(', '))
  const [refusal, setRefusal] = useState(!!shown.expectRefusal)
  const [judge, setJudge] = useState(shown.judge ?? '')
  const [note, setNote] = useState('')
  const [error, setError] = useState<string | null>(null)

  const act = async (f: () => Promise<unknown>) => {
    setError(null)
    try { await f(); onDone() } catch (e) { setError(e instanceof Error ? e.message : String(e)) }
  }
  const edited = (): MinedCase => ({
    id: c.draft.id, question, expectedTools: list(tools).length ? list(tools) : undefined, forbiddenTools: list(forbid).length ? list(forbid) : undefined,
    expectRefusal: refusal || undefined, judge: judge.trim() || undefined,
  })

  return (
    <li className="card">
      <p className="small muted">
        <strong>{c.problem}</strong> · {c.signature} · {t('{n} runs', { n: c.runs })} · {t('impact {n}', { n: c.impact })}
      </p>
      <p className="small">{c.runIds.slice(0, 5).map((id) => <a key={id} href={`#/runs/${id}`} className="mono">{id.slice(0, 8)} </a>)}</p>
      {!edit && (
        <>
          <p><strong>{shown.question}</strong></p>
          <p className="small muted">
            {shown.expectedTools && <>{t('expected tools')}: <span className="mono">{shown.expectedTools.join(', ')}</span> · </>}
            {shown.forbiddenTools && <>{t('forbidden tools')}: <span className="mono">{shown.forbiddenTools.join(', ')}</span> · </>}
            {shown.expectRefusal && <>{t('should refuse')} · </>}
            {shown.judge}
          </p>
        </>
      )}
      {edit && (
        <div className="casebox">
          <label>{t('Question (remove personal data: it goes into a dataset file)')}<textarea rows={2} value={question} onChange={(e) => setQuestion(e.target.value)} /></label>
          <label>{t('Expected tools (comma separated)')}<input value={tools} onChange={(e) => setTools(e.target.value)} /></label>
          <label>{t('Tools it must not call')}<input value={forbid} onChange={(e) => setForbid(e.target.value)} /></label>
          <label className="inline"><input type="checkbox" checked={refusal} onChange={(e) => setRefusal(e.target.checked)} /> {t('It should refuse (policy does not allow it)')}</label>
          <label>{t('What a correct answer does (judge criteria)')}<textarea rows={2} value={judge} onChange={(e) => setJudge(e.target.value)} /></label>
        </div>
      )}
      {c.state === 'Open' && (
        <div className="row approval-actions">
          <input className="wide" aria-label={t('Note')} placeholder={t('Note (optional)')} value={note} onChange={(e) => setNote(e.target.value)} />
          <button type="button" className="btn ghost" onClick={() => setEdit((x) => !x)}>{edit ? t('Cancel') : t('Edit')}</button>
          <button type="button" className="btn" onClick={() => void act(() => api.rejectCandidate(c.id, note))}>{t('Reject')}</button>
          <button type="button" className="btn primary" onClick={() => void act(() => api.acceptCandidate(c.id, edit ? edited() : shown, note))}>{t('Accept')}</button>
        </div>
      )}
      {c.state !== 'Open' && <p className="small muted">{c.state} · {c.reviewedBy}{c.reviewNote ? ` · ${c.reviewNote}` : ''}</p>}
      {error && <p role="alert" className="error">{error}</p>}
    </li>
  )
}

function Proposals({ api, onForbidden }: { api: Api; onForbidden: () => void }) {
  const { data, error, reload } = useLoad<Proposal[]>(() => api.proposals(), [], onForbidden)
  return (
    <>
      <p className="muted summary">{t('Changes to profile instructions, description or model, drafted from the evidence. Evaluate one with Lots.Evals --mode proposal, open its pull request, and merge it in the configuration repository; the follow-up shows whether it helped.')}</p>
      {error && <p role="alert" className="error">{error}</p>}
      {data && data.length === 0 && <p className="muted">{t('No proposals yet.')}</p>}
      <ul className="feedback-list">
        {data?.map((p) => <ProposalItem key={p.id} api={api} p={p} onDone={reload} />)}
      </ul>
    </>
  )
}

function ProposalItem({ api, p, onDone }: { api: Api; p: Proposal; onDone: () => void }) {
  const [error, setError] = useState<string | null>(null)
  const [follow, setFollow] = useState<FollowUp | null>(null)
  const act = async (f: () => Promise<unknown>) => {
    setError(null)
    try { await f(); onDone() } catch (e) { setError(e instanceof Error ? e.message : String(e)) }
  }
  return (
    <li className="card">
      <p className="small muted">
        <strong>{p.state}</strong> · {p.profile} v{p.baseVersion} → v{p.baseVersion + 1} · {p.createdBy} · {fmt.dateTime(p.createdAt)}
        {p.prUrl && <> · <a href={p.prUrl} target="_blank" rel="noreferrer noopener">{t('pull request')}</a></>}
      </p>
      <p><strong>{p.title}</strong></p>
      <p className="small">{p.rationale}</p>
      {p.evaluation && (
        <p className={`small ${p.regresses ? 'error' : ''}`}>
          {t('Evals: current {a}, proposed {b}', { a: pct(p.evaluation.current.passRate), b: pct(p.evaluation.proposed.passRate) })}
          {p.evaluation.regressions.length > 0 && <> · {t('regressed')}: {p.evaluation.regressions.join(', ')}</>}
          {p.evaluation.improvements.length > 0 && <> · {t('fixed')}: {p.evaluation.improvements.join(', ')}</>}
        </p>
      )}
      <details>
        <summary className="small">{t('Diff')}</summary>
        <pre className="diff">{p.diff}</pre>
      </details>
      <div className="row">
        {p.state === 'Evaluated' && <button type="button" className="btn primary" onClick={() => void act(() => api.openPullRequest(p.id))}>{t('Open pull request')}</button>}
        {(p.state === 'PrOpened' || p.state === 'Merged') && (
          <button type="button" className="btn" onClick={() => void api.followUp(p.id).then(setFollow, (e) => setError(String(e)))}>{t('Check the effect')}</button>
        )}
        {(p.state === 'Draft' || p.state === 'Evaluated') && <button type="button" className="btn" onClick={() => void act(() => api.rejectProposal(p.id))}>{t('Reject')}</button>}
      </div>
      {follow && <p className={`small ${follow.verdict === 'regressed' ? 'error' : 'muted'}`}><strong>{follow.verdict}</strong> · {follow.explanation}</p>}
      {follow?.revertDiff && <pre className="diff">{follow.revertDiff}</pre>}
      {error && <p role="alert" className="error">{error}</p>}
    </li>
  )
}
