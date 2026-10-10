import { useEffect, useState } from 'react'
import { ApiError, auditQuery, type Api, type AuditEntry, type AuditFilter } from '../api'

type Load =
  | { state: 'loading' }
  | { state: 'forbidden' }
  | { state: 'error'; message: string }
  | { state: 'ok'; rows: AuditEntry[] }

/** Downloads the filtered audit log as CSV or JSON lines (with the chain position and hash of every row). */
async function exportAudit(api: Api, f: AuditFilter, format: 'csv' | 'json') {
  const q = auditQuery(f)
  q.set('format', format)
  const res = await fetch(`/audit/export?${q}`, { headers: await api.authHeaders() })
  const url = URL.createObjectURL(await res.blob())
  const a = document.createElement('a')
  a.href = url
  a.download = `lots-audit.${format === 'json' ? 'jsonl' : 'csv'}`
  a.click()
  URL.revokeObjectURL(url)
}

function ChainStatus({ api }: { api: Api }) {
  const [c, setC] = useState<{ intact: boolean; sealed: number; firstBrokenSeq: number | null; problem: string | null; unsealed: number } | null>(null)
  useEffect(() => {
    api.raw<NonNullable<typeof c>>('/audit/verify').then(setC).catch(() => setC(null))
  }, [api])
  if (!c) return null
  return (
    <p className={c.intact ? 'muted small' : 'error'}>
      {c.intact
        ? `Tamper check: the hash chain of ${c.sealed.toLocaleString()} sealed entries is intact${c.unsealed ? ` (${c.unsealed} waiting to be sealed)` : ''}.`
        : `Tamper check FAILED at entry ${c.firstBrokenSeq}: ${c.problem}.`}
    </p>
  )
}

export default function AuditPage({ api }: { api: Api }) {
  const initial: AuditFilter = { runId: new URLSearchParams(window.location.hash.split('?')[1] ?? '').get('runId') ?? undefined }
  const [filter, setFilter] = useState<AuditFilter>(initial)
  const [applied, setApplied] = useState<AuditFilter>(initial)
  const [load, setLoad] = useState<Load>({ state: 'loading' })

  useEffect(() => {
    let cancelled = false
    api
      .listAudit(applied)
      .then((rows) => !cancelled && setLoad({ state: 'ok', rows }))
      .catch((e: unknown) => {
        if (cancelled) return
        if (e instanceof ApiError && e.status === 403) setLoad({ state: 'forbidden' })
        else setLoad({ state: 'error', message: e instanceof Error ? e.message : String(e) })
      })
    return () => {
      cancelled = true
    }
  }, [api, applied])

  return (
    <section>
      <h1>Audit</h1>
      <ChainStatus api={api} />
      {load.state === 'forbidden' ? (
        <div className="empty">
          <p>The audit log is only available to admins and auditors.</p>
        </div>
      ) : (
        <>
          <form
            className="filters"
            onSubmit={(e) => {
              e.preventDefault()
              setLoad({ state: 'loading' })
              setApplied({ ...filter })
            }}
          >
            <label>
              User
              <input value={filter.user ?? ''} onChange={(e) => setFilter({ ...filter, user: e.target.value })} />
            </label>
            <label>
              Run id
              <input className="wide" value={filter.runId ?? ''} onChange={(e) => setFilter({ ...filter, runId: e.target.value })} />
            </label>
            <label>
              Tool
              <input value={filter.tool ?? ''} onChange={(e) => setFilter({ ...filter, tool: e.target.value })} />
            </label>
            <label>
              Decision
              <select value={filter.decision ?? ''} onChange={(e) => setFilter({ ...filter, decision: e.target.value || undefined })}>
                <option value="">Any</option>
                {['Allowed', 'Denied', 'ApprovalRequested', 'ApprovalGranted', 'ApprovalRefused', 'ApprovalDenied'].map((d) => (
                  <option key={d}>{d}</option>
                ))}
              </select>
            </label>
            <label>
              Profile
              <input value={filter.profile ?? ''} onChange={(e) => setFilter({ ...filter, profile: e.target.value })} />
            </label>
            <label>
              From
              <input type="datetime-local" value={filter.from ?? ''} onChange={(e) => setFilter({ ...filter, from: e.target.value })} />
            </label>
            <label>
              To
              <input type="datetime-local" value={filter.to ?? ''} onChange={(e) => setFilter({ ...filter, to: e.target.value })} />
            </label>
            <button className="btn" type="submit">
              Filter
            </button>
            <button className="btn" type="button" onClick={() => void exportAudit(api, applied, 'csv')}>
              Export CSV
            </button>
            <button className="btn" type="button" onClick={() => void exportAudit(api, applied, 'json')}>
              Export JSON
            </button>
          </form>

          {load.state === 'loading' && <p className="muted">Loading…</p>}
          {load.state === 'error' && (
            <p role="alert" className="error">
              Could not load the audit log: {load.message}
            </p>
          )}
          {load.state === 'ok' && load.rows.length === 0 && (
            <div className="empty">
              <p>No audit entries match.</p>
            </div>
          )}
          {load.state === 'ok' && load.rows.length > 0 && (
            <table>
              <thead>
                <tr>
                  <th>Time</th>
                  <th>User</th>
                  <th>Tool</th>
                  <th>Decision</th>
                  <th>Approver</th>
                  <th>Result</th>
                  <th>Backend auth</th>
                  <th>Profile</th>
                </tr>
              </thead>
              <tbody>
                {load.rows.map((r) => (
                  <tr key={r.id} title={r.reason || undefined}>
                    <td className="mono">{new Date(r.at).toLocaleString()}</td>
                    <td>{r.user}</td>
                    <td>
                      <a href={`#/runs/${r.runId}`} className="mono">
                        {r.tool}
                      </a>
                    </td>
                    <td>
                      <span className={`decision ${r.decision}`}>{r.decision}</span>
                    </td>
                    <td>{r.approver ?? ''}</td>
                    <td>{r.result ?? ''}</td>
                    <td className="muted">{r.backendAuth ?? ''}</td>
                    <td className="muted">
                      {r.profile} v{r.profileVersion}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </>
      )}
    </section>
  )
}
