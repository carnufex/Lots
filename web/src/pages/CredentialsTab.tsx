import { useCallback, useEffect, useState } from 'react'
import type { Api } from '../api'

interface Credential {
  server: string
  profiles: string
  auth: string
  type: string | null
  secretReferences: string[]
  status: { lastFetched: string | null; expiresAt: string | null; lastUsed: string | null; lastError: string | null } | null
  userConnected: boolean
  myConnection: { connectedAt: string; expiresAt: string | null; lastUsedAt: string | null; scope: string | null } | null
}

const when = (s: string | null | undefined) => (s ? new Date(s).toLocaleString() : '–')

/** Credentials (#62): secret references and live status (admins), and the caller's own connected accounts. Values are never shown. */
export default function CredentialsTab({ api }: { api: Api }) {
  const [list, setList] = useState<Credential[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const params = new URLSearchParams(window.location.hash.split('?')[1] ?? '')
  const load = useCallback(() => {
    api
      .raw<Credential[]>('/integrations/credentials')
      .then(setList)
      .catch((e: unknown) => setError(String(e)))
  }, [api])
  useEffect(load, [load])

  const connect = async (server: string) => {
    const { authorizeUrl } = await api.raw<{ authorizeUrl: string }>(`/integrations/connections/${encodeURIComponent(server)}`, { method: 'POST', body: '{}' })
    window.location.href = authorizeUrl
  }

  return (
    <>
      {params.get('connected') && <p className="small">Connected your account for {params.get('connected')}.</p>}
      {params.get('error') && <p className="error small">Connecting failed: {params.get('error')}</p>}
      {error && <p className="error">{error}</p>}
      <p className="muted small">
        Profiles reference secrets by name (an environment variable or <span className="mono">file:/path</span> for mounted Kubernetes, Vault or Bitwarden secrets; files
        are re-read, so rotation needs no restart). Values are never stored or shown here.
      </p>
      {list && list.length === 0 && (
        <div className="empty">
          <p>No credentials to show.</p>
        </div>
      )}
      {list && list.length > 0 && (
        <table>
          <thead>
            <tr>
              <th>Server</th>
              <th>Identity</th>
              <th>Secrets from</th>
              <th>Status</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {list.map((c) => (
              <tr key={c.server}>
                <td>
                  {c.server} <span className="muted small">{c.profiles}</span>
                </td>
                <td>
                  {c.userConnected ? 'your own account' : c.auth}
                  {c.type && <span className="muted small"> · {c.type}</span>}
                </td>
                <td className="muted small">{c.secretReferences.join(', ')}</td>
                <td className="small">
                  {c.userConnected ? (
                    c.myConnection ? (
                      <>
                        connected {when(c.myConnection.connectedAt)} · token until {when(c.myConnection.expiresAt)} · last used {when(c.myConnection.lastUsedAt)}
                      </>
                    ) : (
                      <span className="warn">not connected</span>
                    )
                  ) : c.status ? (
                    <>
                      fetched {when(c.status.lastFetched)} · expires {when(c.status.expiresAt)} · used {when(c.status.lastUsed)}
                      {c.status.lastError && <span className="error"> · {c.status.lastError}</span>}
                    </>
                  ) : null}
                </td>
                <td>
                  {c.userConnected &&
                    (c.myConnection ? (
                      <button
                        type="button"
                        className="btn small"
                        onClick={() => void api.raw(`/integrations/connections/${encodeURIComponent(c.server)}`, { method: 'DELETE' }).then(load)}
                      >
                        Disconnect
                      </button>
                    ) : (
                      <button type="button" className="btn small primary" onClick={() => void connect(c.server).catch((e: unknown) => setError(String(e)))}>
                        Connect
                      </button>
                    ))}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  )
}
