import { useEffect, useState } from 'react'
import type { Api } from '../api'

interface UsageRow {
  key: string
  runs: number
  failedRuns: number
  modelCalls: number
  promptTokens: number
  completionTokens: number
  cost: number
  modelSecondsP95: number
  errorRate: number
}

interface UsageReport {
  currency: string
  groupBy: string
  from: string
  to: string
  rows: UsageRow[]
  total: UsageRow
  unpricedModels: string[]
}

const GROUPS = [
  ['day', 'Per day'],
  ['model', 'Per model'],
  ['profile', 'Per profile'],
  ['user', 'Per user (admins)'],
] as const

/** Bars for one measure, scaled to the largest value. */
function Bars({ rows, value, format }: { rows: UsageRow[]; value: (r: UsageRow) => number; format: (n: number) => string }) {
  const max = Math.max(...rows.map(value), 0) || 1
  return (
    <div className="bars" role="img" aria-label="chart">
      {rows.map((r) => (
        <div key={r.key} className="bar" title={`${r.key}: ${format(value(r))}`}>
          <div className="fill" style={{ height: `${(value(r) / max) * 100}%` }} />
          <span className="small muted">{r.key.slice(5)}</span>
        </div>
      ))}
    </div>
  )
}

interface Quota {
  limits: { runsPerMinute: number | null; concurrentRuns: number | null; tokensPerDay: number | null; toolCallsPerRun: number | null; speechSecondsPerDay: number | null }
  usage: { runsLastMinute: number; activeRuns: number; tokensToday: number; speechSecondsToday: number }
}

/** Your limits (#78) and how much of today's budgets is used. */
function QuotaCard({ api }: { api: Api }) {
  const [q, setQ] = useState<Quota | null>(null)
  useEffect(() => {
    api.raw<Quota>('/me/quota').then(setQ).catch(() => setQ(null))
  }, [api])
  if (!q) return null
  const part = (used: number, limit: number | null, unit = '') =>
    limit === null ? <span className="muted">no limit</span> : (
      <span className={used >= limit ? 'error' : used >= limit * 0.8 ? 'warn' : undefined}>
        {used.toLocaleString()} / {limit.toLocaleString()}
        {unit}
      </span>
    )
  return (
    <div className="card small">
      Your limits: tokens today {part(q.usage.tokensToday, q.limits.tokensPerDay)} · voice today {part(Math.round(q.usage.speechSecondsToday), q.limits.speechSecondsPerDay, ' s')} · runs in progress{' '}
      {part(q.usage.activeRuns, q.limits.concurrentRuns)} · per minute {part(q.usage.runsLastMinute, q.limits.runsPerMinute)} · tool calls per run{' '}
      {q.limits.toolCallsPerRun ?? 'no limit'}
    </div>
  )
}

/** Usage (#77): tokens, cost, latency and failures over time or per model, profile or user. */
export default function UsagePage({ api }: { api: Api }) {
  const [groupBy, setGroupBy] = useState<string>('day')
  const [days, setDays] = useState(30)
  const [report, setReport] = useState<UsageReport | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const from = new Date(Date.now() - days * 86_400_000).toISOString()
    api
      .raw<UsageReport>(`/usage?groupBy=${groupBy}&from=${encodeURIComponent(from)}`)
      .then(setReport)
      .catch((e: unknown) => setError(String(e)))
  }, [api, groupBy, days])

  const money = (n: number) => `${n.toFixed(n < 1 ? 4 : 2)} ${report?.currency ?? ''}`
  const tokens = (r: UsageRow) => r.promptTokens + r.completionTokens

  return (
    <section>
      <h1>Usage</h1>
      <QuotaCard api={api} />
      <div className="filters">
        <label>
          Group
          <select value={groupBy} onChange={(e) => setGroupBy(e.target.value)}>
            {GROUPS.map(([v, l]) => (
              <option key={v} value={v}>
                {l}
              </option>
            ))}
          </select>
        </label>
        <label>
          Period
          <select value={days} onChange={(e) => setDays(Number(e.target.value))}>
            <option value={1}>24 hours</option>
            <option value={7}>7 days</option>
            <option value={30}>30 days</option>
            <option value={90}>90 days</option>
          </select>
        </label>
      </div>
      {error && <p className="error">{error}</p>}
      {report && (
        <>
          <div className="card">
            <dl className="meta">
              <div>
                <dt>Runs</dt>
                <dd>
                  {report.total.runs} <span className="muted small">({(report.total.errorRate * 100).toFixed(1)} % failed)</span>
                </dd>
              </div>
              <div>
                <dt>Tokens</dt>
                <dd>{tokens(report.total).toLocaleString()}</dd>
              </div>
              <div>
                <dt>Cost</dt>
                <dd>{money(report.total.cost)}</dd>
              </div>
              <div>
                <dt>Model p95</dt>
                <dd>{report.total.modelSecondsP95} s</dd>
              </div>
            </dl>
            {report.unpricedModels.length > 0 && (
              <p className="muted small">
                No price configured for {report.unpricedModels.join(', ')} (Models:Prices): counted as 0.
              </p>
            )}
          </div>
          {report.groupBy === 'day' && report.rows.length > 1 && (
            <>
              <h2 className="section-title">Tokens per day</h2>
              <Bars rows={report.rows} value={tokens} format={(n) => n.toLocaleString()} />
              <h2 className="section-title">Cost per day</h2>
              <Bars rows={report.rows} value={(r) => r.cost} format={money} />
            </>
          )}
          <table>
            <thead>
              <tr>
                <th>{report.groupBy}</th>
                <th>Runs</th>
                <th>Failed</th>
                <th>Model calls</th>
                <th>Tokens (in / out)</th>
                <th>Cost</th>
                <th>Model p95</th>
              </tr>
            </thead>
            <tbody>
              {report.rows.map((r) => (
                <tr key={r.key}>
                  <td className="mono">{r.key}</td>
                  <td>{r.runs}</td>
                  <td>{r.failedRuns}</td>
                  <td>{r.modelCalls}</td>
                  <td>
                    {r.promptTokens.toLocaleString()} / {r.completionTokens.toLocaleString()}
                  </td>
                  <td>{money(r.cost)}</td>
                  <td>{r.modelSecondsP95} s</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </section>
  )
}
