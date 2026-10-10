import { useEffect, useState } from 'react'
import { ApiError, type Api, type CatalogEntry, type ServerInfo } from '../api'

type Load<T> = { state: 'loading' } | { state: 'forbidden' } | { state: 'error'; message: string } | { state: 'ok'; data: T }

function useLoad<T>(fetch: () => Promise<T>, deps: unknown[]): [Load<T>, () => void] {
  const [load, setLoad] = useState<Load<T>>({ state: 'loading' })
  const [nonce, setNonce] = useState(0)
  useEffect(() => {
    let cancelled = false
    setLoad({ state: 'loading' })
    fetch()
      .then((data) => !cancelled && setLoad({ state: 'ok', data }))
      .catch((e: unknown) => {
        if (cancelled) return
        if (e instanceof ApiError && e.status === 403) setLoad({ state: 'forbidden' })
        else setLoad({ state: 'error', message: e instanceof Error ? e.message : String(e) })
      })
    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, nonce])
  return [load, () => setNonce((n) => n + 1)]
}

function Status<T>({ load, who, what }: { load: Load<T>; who: string; what: string }) {
  if (load.state === 'loading') return <p className="muted">Loading…</p>
  if (load.state === 'forbidden')
    return (
      <div className="empty">
        <p>{what} is only available to {who}.</p>
      </div>
    )
  if (load.state === 'error')
    return (
      <p role="alert" className="error">
        Could not load {what.toLowerCase()}: {load.message}
      </p>
    )
  return null
}

const healthClass = { ok: 'Allowed', unavailable: 'Denied', 'per-user': 'ApprovalRequested', unknown: 'ApprovalRequested' } as Record<string, string>

/** Tool servers used by the profiles: address, backend auth, health and the tools each offers. Admins only. */
export function ServersTab({ api }: { api: Api }) {
  const [load, reload] = useLoad<ServerInfo[]>(() => api.listServers(), [api])
  return (
    <>
      <p className="muted small">
        Servers come from the profiles (config as code). Tools a server offers but no profile declares are never shown to the model.{' '}
        <button type="button" className="btn small" onClick={reload}>
          Check again
        </button>
      </p>
      <Status load={load} who="admins" what="Server details" />
      {load.state === 'ok' && load.data.length === 0 && (
        <div className="empty">
          <p>No profile uses a tool server.</p>
        </div>
      )}
      {load.state === 'ok' &&
        load.data.map((s) => (
          <div key={s.name} className="card">
            <div className="meta">
              <strong>{s.name}</strong>
              <span className={`decision ${healthClass[s.health] ?? 'ApprovalRequested'}`}>{s.health}</span>
              <span className="muted mono small">{s.url}</span>
              <span className="muted small">
                auth: {s.auth}
                {s.credentialType ? ` (${s.credentialType})` : ''} · profiles: {s.profiles.join(', ')}
                {s.checkedAt ? ` · checked ${new Date(s.checkedAt).toLocaleTimeString()}` : ''}
              </span>
            </div>
            {s.error && (
              <p className="error small" role="alert">
                {s.error}
              </p>
            )}
            {s.health === 'per-user' && <p className="muted small">Reached with each user's own token, so its tools are only listed inside a user's run.</p>}
            {s.tools.length > 0 && (
              <table>
                <thead>
                  <tr>
                    <th>Tool</th>
                    <th>Description</th>
                    <th>Declared in</th>
                  </tr>
                </thead>
                <tbody>
                  {s.tools.map((t) => (
                    <tr key={t.name}>
                      <td className="mono">{t.name}</td>
                      <td className="muted">{t.description}</td>
                      <td>{t.profiles.length > 0 ? t.profiles.join(', ') : <span className="decision Denied">unclassified</span>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        ))}
    </>
  )
}

const catalogClass = { exposed: 'Allowed', missing: 'ApprovalRequested', 'per-user': 'ApprovalRequested', unclassified: 'Denied' } as Record<string, string>

/** Every declared tool with its risk class, who may use and approve it, and tools that are offered but not classified. */
export function CatalogTab({ api }: { api: Api }) {
  const [load] = useLoad<CatalogEntry[]>(() => api.catalog(), [api])
  const roles = (r: string[]) => (r.length ? r.join(', ') : <span className="muted">none</span>)
  return (
    <>
      <p className="muted small">
        Deny by default: a tool is callable only when a profile declares it with a risk class and a role grants that class. Unclassified tools stay blocked
        until someone adds them to a profile.
      </p>
      <Status load={load} who="admins and auditors" what="The catalog" />
      {load.state === 'ok' && load.data.length === 0 && (
        <div className="empty">
          <p>No tools.</p>
        </div>
      )}
      {load.state === 'ok' && load.data.length > 0 && (
        <table>
          <thead>
            <tr>
              <th>Tool</th>
              <th>Profile</th>
              <th>Risk</th>
              <th>Status</th>
              <th>May use</th>
              <th>Needs approval</th>
              <th>May approve</th>
              <th>Calls</th>
              <th>Last used</th>
            </tr>
          </thead>
          <tbody>
            {load.data.map((c) => (
              <tr key={`${c.profile ?? '-'}:${c.server ?? '-'}:${c.tool}`} title={c.description || undefined}>
                <td className="mono">{c.tool}</td>
                <td>{c.profile ?? <span className="muted">—</span>}</td>
                <td>{c.risk ?? <span className="muted">—</span>}</td>
                <td>
                  <span className={`decision ${catalogClass[c.status] ?? 'ApprovalRequested'}`}>{c.status}</span>
                </td>
                <td>{roles(c.allowedRoles)}</td>
                <td>{roles(c.approvalRoles)}</td>
                <td>{roles(c.approverRoles)}</td>
                <td>{c.calls}</td>
                <td className="muted">{c.lastUsed ? new Date(c.lastUsed).toLocaleString() : ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  )
}
