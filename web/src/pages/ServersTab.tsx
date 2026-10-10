import { useEffect, useState } from 'react'
import { ApiError, type Api, type CatalogEntry, type ServerInfo } from '../api'
import { fmt, t } from '../i18n'

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
  if (load.state === 'loading') return <p className="muted">{t('Loading…')}</p>
  if (load.state === 'forbidden')
    return (
      <div className="empty">
        <p>{t('{what} is only available to {who}.', { what: t(what), who: t(who) })}</p>
      </div>
    )
  if (load.state === 'error')
    return (
      <p role="alert" className="error">
        {t('Could not load {what}:', { what: t(what).toLowerCase() })} {load.message}
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
          {t('Check again')}
        </button>
      </p>
      <Status load={load} who="admins" what="Server details" />
      {load.state === 'ok' && load.data.length === 0 && (
        <div className="empty">
          <p>{t('No profile uses a tool server.')}</p>
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
                {s.checkedAt ? ` · checked ${fmt.time(s.checkedAt)}` : ''}
              </span>
            </div>
            {s.error && (
              <p className="error small" role="alert">
                {s.error}
              </p>
            )}
            {s.health === 'per-user' && <p className="muted small">{t("Reached with each user's own token, so its tools are only listed inside a user's run.")}</p>}
            {s.tools.length > 0 && (
              <table>
                <thead>
                  <tr>
                    <th>{t('Tool')}</th>
                    <th>{t('Description')}</th>
                    <th>{t('Declared in')}</th>
                  </tr>
                </thead>
                <tbody>
                  {s.tools.map((it) => (
                    <tr key={it.name}>
                      <td className="mono">{it.name}</td>
                      <td className="muted">{it.description}</td>
                      <td>{it.profiles.length > 0 ? it.profiles.join(', ') : <span className="decision Denied">{t('unclassified')}</span>}</td>
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
  const roles = (r: string[]) => (r.length ? r.join(', ') : <span className="muted">{t('none')}</span>)
  return (
    <>
      <p className="muted small">
        {t('Deny by default: a tool is callable only when a profile declares it with a risk class and a role grants that class. Unclassified tools stay blocked until someone adds them to a profile.')}
      </p>
      <Status load={load} who="admins and auditors" what="The catalog" />
      {load.state === 'ok' && load.data.length === 0 && (
        <div className="empty">
          <p>{t('No tools.')}</p>
        </div>
      )}
      {load.state === 'ok' && load.data.length > 0 && (
        <table>
          <thead>
            <tr>
              <th>{t('Tool')}</th>
              <th>{t('Context')}</th>
              <th>{t('Risk')}</th>
              <th>{t('Status')}</th>
              <th>{t('May use')}</th>
              <th>{t('Needs approval')}</th>
              <th>{t('May approve')}</th>
              <th>{t('Calls')}</th>
              <th>{t('Last used')}</th>
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
                <td className="muted">{c.lastUsed ? fmt.dateTime(c.lastUsed) : ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  )
}
