import { Fragment, useEffect, useState } from 'react'
import type { Api, ToolCallFilter, ToolCallList } from '../api'
import ToolResult from '../components/ToolResult'
import { fmt, t } from '../i18n'

type Load = { state: 'loading' } | { state: 'error'; message: string } | { state: 'ok'; data: ToolCallList }

const statusClass = { ok: 'Allowed', error: 'ApprovalRequested', denied: 'Denied' } as const

/** Every tool call across the caller's runs (all runs for admins and auditors), with the policy decision and per-tool stats. */
export default function ToolCallsTab({ api }: { api: Api }) {
  const [filter, setFilter] = useState<ToolCallFilter>({})
  const [applied, setApplied] = useState<ToolCallFilter>({})
  const [load, setLoad] = useState<Load>({ state: 'loading' })
  const [open, setOpen] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    api
      .listToolCalls(applied)
      .then((data) => !cancelled && setLoad({ state: 'ok', data }))
      .catch((e: unknown) => !cancelled && setLoad({ state: 'error', message: e instanceof Error ? e.message : String(e) }))
    return () => {
      cancelled = true
    }
  }, [api, applied])

  const apply = (f: ToolCallFilter) => {
    setFilter(f)
    setLoad({ state: 'loading' })
    setApplied({ ...f })
  }

  const text = (key: 'user' | 'profile' | 'tool', label: string) => (
    <label>
      {label}
      <input value={filter[key] ?? ''} onChange={(e) => setFilter({ ...filter, [key]: e.target.value })} />
    </label>
  )

  return (
    <>
      <form
        className="filters"
        onSubmit={(e) => {
          e.preventDefault()
          apply(filter)
        }}
      >
        {text('tool', t('Tool'))}
        {text('user', t('User'))}
        {text('profile', t('Profile'))}
        <label>
          {t('Status')}
          <select value={filter.status ?? ''} onChange={(e) => setFilter({ ...filter, status: e.target.value || undefined })}>
            <option value="">{t('Any')}</option>
            <option value="ok">{t('ok')}</option>
            <option value="error">{t('error')}</option>
            <option value="denied">{t('denied')}</option>
          </select>
        </label>
        <label>
          {t('From')}
          <input type="datetime-local" value={filter.from ?? ''} onChange={(e) => setFilter({ ...filter, from: e.target.value })} />
        </label>
        <label>
          {t('To')}
          <input type="datetime-local" value={filter.to ?? ''} onChange={(e) => setFilter({ ...filter, to: e.target.value })} />
        </label>
        <button className="btn" type="submit">
          {t('Filter')}
        </button>
      </form>

      {load.state === 'loading' && <p className="muted">{t('Loading…')}</p>}
      {load.state === 'error' && (
        <p role="alert" className="error">
          {t('Could not load tool calls:')} {load.message}
        </p>
      )}
      {load.state === 'ok' && load.data.total === 0 && (
        <div className="empty">
          <p>{t('No tool calls match.')}</p>
        </div>
      )}
      {load.state === 'ok' && load.data.total > 0 && (
        <>
          <h2 className="section-title">{t('Per tool')}</h2>
          <table>
            <thead>
              <tr>
                <th>{t('Tool')}</th>
                <th>{t('Calls')}</th>
                <th>{t('Errors')}</th>
                <th>{t('Denied')}</th>
                <th>{t('Error rate')}</th>
                <th>{t('p50')}</th>
                <th>{t('p95')}</th>
              </tr>
            </thead>
            <tbody>
              {load.data.tools.map((it) => (
                <tr key={it.tool}>
                  <td>
                    <a
                      href="#/integrations/tool-calls"
                      className="mono"
                      onClick={(e) => {
                        e.preventDefault()
                        apply({ ...applied, tool: it.tool })
                      }}
                    >
                      {it.tool}
                    </a>
                  </td>
                  <td>{it.calls}</td>
                  <td>{it.errors}</td>
                  <td>{it.denied}</td>
                  <td>{(it.errorRate * 100).toFixed(1)} %</td>
                  <td className="mono">{it.p50Ms} ms</td>
                  <td className="mono">{it.p95Ms} ms</td>
                </tr>
              ))}
            </tbody>
          </table>

          <h2 className="section-title">
            Calls{' '}
            <span className="muted small">
              ({load.data.calls.length} of {load.data.total}
              {load.data.truncated ? ', newest 5000 considered' : ''})
            </span>
          </h2>
          <table>
            <thead>
              <tr>
                <th>{t('Time')}</th>
                <th>{t('User')}</th>
                <th>{t('Tool')}</th>
                <th>{t('Status')}</th>
                <th>{t('Decision')}</th>
                <th>{t('Latency')}</th>
                <th>{t('Profile')}</th>
              </tr>
            </thead>
            <tbody>
              {load.data.calls.map((c) => {
                const key = `${c.runId}:${c.seq}`
                const expanded = open === key
                return (
                  <Fragment key={key}>
                    <tr title={c.reason ?? undefined}>
                      <td className="mono">{fmt.dateTime(c.at)}</td>
                      <td>{c.user}</td>
                      <td>
                        <button type="button" className="btn ghost small mono" aria-expanded={expanded} onClick={() => setOpen(expanded ? null : key)}>
                          {c.tool}
                        </button>
                      </td>
                      <td>
                        <span className={`decision ${statusClass[c.status]}`}>{c.status}</span>
                      </td>
                      <td className="muted">
                        {c.decision ?? ''}
                        {c.approver ? ` by ${c.approver}` : ''}
                      </td>
                      <td className="mono">{c.latencyMs} ms</td>
                      <td className="muted">{c.profile}</td>
                    </tr>
                    {expanded && (
                      <tr>
                        <td colSpan={7}>
                          {/* Tool data is untrusted: rendered as plain text only. */}
                          <div className="muted small">{t('Arguments')}</div>
                          <pre>{c.arguments ?? ''}</pre>
                          <div className="muted small">{t('Result')}</div>
                          <ToolResult text={c.result ?? ''} />
                          <p className="small">
                            {c.reason && <span className="muted">{t('Policy:')} {c.reason} · </span>}
                            {c.backendAuth && <span className="muted">{t('Backend auth:')} {c.backendAuth} · </span>}
                            <a href={`#/runs/${c.runId}`}>{t('Open run')}</a> · <a href={`#/audit?runId=${c.runId}`}>{t('audit entries')}</a>
                          </p>
                        </td>
                      </tr>
                    )}
                  </Fragment>
                )
              })}
            </tbody>
          </table>
        </>
      )}
    </>
  )
}
