import { useEffect, useState } from 'react'
import type { Api } from '../api'
import MyData from '../components/MyData'
import ApiTokens from '../components/ApiTokens'
import { t, fmt } from '../i18n'

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
  ['day', t('Per day')],
  ['model', t('Per model')],
  ['profile', t('Per profile')],
  ['user', t('Per user (admins)')],
] as const

/** Bars for one measure, scaled to the largest value. */
function Bars({ rows, value, format }: { rows: UsageRow[]; value: (r: UsageRow) => number; format: (n: number) => string }) {
  const max = Math.max(...rows.map(value), 0) || 1
  return (
    <div className="bars" role="img" aria-label={t('chart')}>
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
    limit === null ? <span className="muted">{t('no limit')}</span> : (
      <span className={used >= limit ? 'error' : used >= limit * 0.8 ? 'warn' : undefined}>
        {fmt.number(used)} / {fmt.number(limit)}
        {unit}
      </span>
    )
  return (
    <div className="card small">
      {t('Your limits: tokens today')} {part(q.usage.tokensToday, q.limits.tokensPerDay)} · {t('voice today')}{' '}
      {part(Math.round(q.usage.speechSecondsToday), q.limits.speechSecondsPerDay, ' s')} · {t('runs in progress')}{' '}
      {part(q.usage.activeRuns, q.limits.concurrentRuns)} · {t('per minute')} {part(q.usage.runsLastMinute, q.limits.runsPerMinute)} · {t('tool calls per run')}{' '}
      {q.limits.toolCallsPerRun ?? t('no limit')}
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
      <h1>{t('Usage')}</h1>
      <QuotaCard api={api} />
      <MyData api={api} />
      <ApiTokens api={api} />
      <div className="filters">
        <label>
          {t('Group')}
          <select value={groupBy} onChange={(e) => setGroupBy(e.target.value)}>
            {GROUPS.map(([v, l]) => (
              <option key={v} value={v}>
                {l}
              </option>
            ))}
          </select>
        </label>
        <label>
          {t('Period')}
          <select value={days} onChange={(e) => setDays(Number(e.target.value))}>
            <option value={1}>{t('24 hours')}</option>
            <option value={7}>{t('7 days')}</option>
            <option value={30}>{t('30 days')}</option>
            <option value={90}>{t('90 days')}</option>
          </select>
        </label>
      </div>
      {error && <p className="error">{error}</p>}
      {report && (
        <>
          <div className="card">
            <dl className="meta">
              <div>
                <dt>{t('Runs')}</dt>
                <dd>
                  {report.total.runs} <span className="muted small">({(report.total.errorRate * 100).toFixed(1)} % failed)</span>
                </dd>
              </div>
              <div>
                <dt>{t('Tokens')}</dt>
                <dd>{fmt.number(tokens(report.total))}</dd>
              </div>
              <div>
                <dt>{t('Cost')}</dt>
                <dd>{money(report.total.cost)}</dd>
              </div>
              <div>
                <dt>{t('Model p95')}</dt>
                <dd>{report.total.modelSecondsP95} s</dd>
              </div>
            </dl>
            {report.unpricedModels.length > 0 && (
              <p className="muted small">
                {t('No price configured for {models} (Models:Prices): counted as 0.', { models: report.unpricedModels.join(', ') })}
              </p>
            )}
          </div>
          {report.groupBy === 'day' && report.rows.length > 1 && (
            <>
              <h2 className="section-title">{t('Tokens per day')}</h2>
              <Bars rows={report.rows} value={tokens} format={(n) => fmt.number(n)} />
              <h2 className="section-title">{t('Cost per day')}</h2>
              <Bars rows={report.rows} value={(r) => r.cost} format={money} />
            </>
          )}
          <table>
            <thead>
              <tr>
                <th>{report.groupBy}</th>
                <th>{t('Runs')}</th>
                <th>{t('Failed')}</th>
                <th>{t('Model calls')}</th>
                <th>{t('Tokens (in / out)')}</th>
                <th>{t('Cost')}</th>
                <th>{t('Model p95')}</th>
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
                    {fmt.number(r.promptTokens)} / {fmt.number(r.completionTokens)}
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
