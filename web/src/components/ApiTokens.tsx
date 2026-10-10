import { useCallback, useEffect, useState } from 'react'
import type { Api } from '../api'

interface ApiToken {
  id: string
  name: string
  hint: string
  scopes: string[]
  roles: string[]
  createdAt: string
  expiresAt: string
  lastUsedAt: string | null
  revokedAt: string | null
}

const SCOPES: [string, string][] = [
  ['read', 'read'],
  ['runs', 'start and cancel runs'],
  ['approvals', 'decide approvals'],
  ['admin', 'administration'],
]

const day = (s: string) => new Date(s).toLocaleDateString()

/** Personal API tokens (#88) for scripts, CI and lotsctl. Shown once on creation; always expire; act with your roles at most. */
export default function ApiTokens({ api }: { api: Api }) {
  const [tokens, setTokens] = useState<ApiToken[]>([])
  const [name, setName] = useState('')
  const [scopes, setScopes] = useState<string[]>(['read'])
  const [days, setDays] = useState(30)
  const [created, setCreated] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    api.raw<ApiToken[]>('/me/tokens').then(setTokens).catch((e: unknown) => setError(String(e)))
  }, [api])
  useEffect(load, [load])

  const create = async () => {
    setError(null)
    try {
      const r = await api.raw<{ token: string }>('/me/tokens', {
        method: 'POST',
        body: JSON.stringify({ name, scopes, expiresInDays: days }),
      })
      setCreated(r.token)
      setName('')
      load()
    } catch (e) {
      setError(String(e))
    }
  }

  const revoke = async (id: string) => {
    await api.raw(`/me/tokens/${id}`, { method: 'DELETE' }).catch((e: unknown) => setError(String(e)))
    load()
  }

  const toggle = (s: string) => setScopes((cur) => (cur.includes(s) ? cur.filter((x) => x !== s) : [...cur, s]))
  const active = tokens.filter((t) => !t.revokedAt && new Date(t.expiresAt) > new Date())

  return (
    <div className="card small">
      <strong>API tokens</strong>
      <p className="muted">
        For scripts, CI and <code>lotsctl --token</code>. A token acts as you, with at most your current roles, limited to its
        scopes. It is shown once.
      </p>
      {created && (
        <p>
          New token (copy it now): <code className="mono">{created}</code>{' '}
          <button type="button" className="btn small" onClick={() => void navigator.clipboard.writeText(created)}>
            Copy
          </button>
        </p>
      )}
      <div className="filters">
        <label>
          Name
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. nightly report" maxLength={100} />
        </label>
        <label>
          Expires in
          <select value={days} onChange={(e) => setDays(Number(e.target.value))}>
            {[7, 30, 90].map((d) => (
              <option key={d} value={d}>
                {d} days
              </option>
            ))}
          </select>
        </label>
        {SCOPES.map(([s, label]) => (
          <label key={s} className="inline">
            <input type="checkbox" checked={scopes.includes(s)} onChange={() => toggle(s)} /> {label}
          </label>
        ))}
        <button type="button" className="btn small" disabled={!name.trim() || scopes.length === 0} onClick={() => void create()}>
          Create token
        </button>
      </div>
      {error && <p className="error">{error}</p>}
      {active.length > 0 && (
        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>Token</th>
              <th>Scopes</th>
              <th>Expires</th>
              <th>Last used</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {active.map((t) => (
              <tr key={t.id}>
                <td>{t.name}</td>
                <td className="mono">{t.hint}</td>
                <td>{t.scopes.join(', ')}</td>
                <td>{day(t.expiresAt)}</td>
                <td>{t.lastUsedAt ? new Date(t.lastUsedAt).toLocaleString() : 'never'}</td>
                <td>
                  <button type="button" className="btn small" onClick={() => void revoke(t.id)}>
                    Revoke
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
