import { useEffect, useMemo, useState } from 'react'
import { ApiError, type Api } from '../api'
import DataReport from '../components/DataReport'
import { adminApi, type IdentityInfo, type MappingTest, type UserInfo } from '../adminApi'

/** Identity (#72) and users (#73): login settings, claim mapping, how each backend is reached, a mapping test, who uses Lots. */
export default function IdentityPage({ api }: { api: Api }) {
  const admin = useMemo(() => adminApi(api), [api])
  const [info, setInfo] = useState<IdentityInfo | null>(null)
  const [users, setUsers] = useState<UserInfo[]>([])
  const [error, setError] = useState<string | null>(null)
  const [token, setToken] = useState('')
  const [test, setTest] = useState<MappingTest | null>(null)
  const [testError, setTestError] = useState<string | null>(null)

  useEffect(() => {
    admin
      .identity()
      .then(setInfo)
      .catch((e: unknown) => setError(e instanceof ApiError && e.status === 403 ? 'Identity settings are visible to admins.' : String(e)))
    admin.users().then(setUsers).catch(() => setUsers([]))
  }, [admin])

  return (
    <section>
      <h1>Identity</h1>
      {error && <p className="error">{error}</p>}
      {info && (
        <>
          <div className="card">
            <dl className="meta">
              <div>
                <dt>Login</dt>
                <dd>{info.mode === 'Oidc' ? 'OIDC' : <span className="warn">Dev (local development only)</span>}</dd>
              </div>
              {info.authority && (
                <div>
                  <dt>Authority</dt>
                  <dd className="mono small">{info.authority}</dd>
                </div>
              )}
              <div>
                <dt>Client</dt>
                <dd>{info.clientId ?? '–'}</dd>
              </div>
              <div>
                <dt>User claim</dt>
                <dd className="mono">{info.userClaim}</dd>
              </div>
              <div>
                <dt>Role claim</dt>
                <dd className="mono">
                  {info.roleClaim}
                  {info.rolePrefix ? ` (prefix ${info.rolePrefix})` : ''}
                </dd>
              </div>
              <div>
                <dt>Admins / auditors</dt>
                <dd>
                  {info.adminRoles} / {info.auditRoles}
                </dd>
              </div>
              <div>
                <dt>Roles in profiles</dt>
                <dd>{info.knownRoles.join(', ')}</dd>
              </div>
            </dl>
            {info.devHeaders && <p className="warn small">Dev identity headers are enabled: anyone can act as anyone. Never outside local development.</p>}
          </div>

          <h2 className="section-title">How backends are reached</h2>
          <p className="muted small">Only admins see this; end users never see which account a call used.</p>
          <table>
            <thead>
              <tr>
                <th>Server</th>
                <th>Profiles</th>
                <th>Identity</th>
                <th>Credentials</th>
              </tr>
            </thead>
            <tbody>
              {info.servers.map((s) => (
                <tr key={s.server}>
                  <td>{s.server}</td>
                  <td>{s.profiles}</td>
                  <td>{s.auth === 'delegated' ? "the user's own token (token exchange)" : 'shared service account'}</td>
                  <td className="muted small">
                    {s.credentialType ?? 'none'}
                    {s.tokenUrl ? ` · ${s.tokenUrl}` : ''}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          <h2 className="section-title">Test a login mapping</h2>
          <form
            className="filters"
            onSubmit={(e) => {
              e.preventDefault()
              setTestError(null)
              admin
                .testMapping(token)
                .then(setTest)
                .catch((err: unknown) => setTestError(String(err)))
            }}
          >
            <label className="grow">
              A test user's token (decoded here, never stored)
              <input value={token} onChange={(e) => setToken(e.target.value)} autoComplete="off" spellCheck={false} />
            </label>
            <button className="btn" type="submit" disabled={!token.trim()}>
              Map
            </button>
          </form>
          {testError && <p className="error small">{testError}</p>}
          {test && (
            <p>
              user <strong>{test.user ?? '–'}</strong> · roles {test.roles.join(', ') || 'none'}
              {test.unknownRoles.length > 0 && <span className="warn"> · not used by any profile: {test.unknownRoles.join(', ')}</span>}
              {test.isAdmin && ' · admin'}
              {test.isAuditor && ' · auditor'}
              <span className="muted small">
                {' '}
                · {test.issuer} · expires {test.expiresAt ? new Date(test.expiresAt).toLocaleString() : '–'} · {test.note}
              </span>
            </p>
          )}
        </>
      )}

      <DataReport api={api} />
      {users.length > 0 && (
        <>
          <h2 className="section-title">Users</h2>
          <p className="muted small">People who used Lots. Accounts and groups live in the identity provider; roles shown are from each user's latest run.</p>
          <table>
            <thead>
              <tr>
                <th>User</th>
                <th>Roles</th>
                <th>Last active</th>
                <th>Runs (30 d)</th>
                <th>Tokens (30 d)</th>
                <th>Approvals (30 d)</th>
                <th>Personal data</th>
              </tr>
            </thead>
            <tbody>
              {users.map((u) => (
                <tr key={u.user}>
                  <td>{u.user}</td>
                  <td>{u.roles.join(', ')}</td>
                  <td className="muted">{new Date(u.lastActive).toLocaleString()}</td>
                  <td>
                    {u.runs30Days} <span className="muted small">/ {u.runs}</span>
                  </td>
                  <td>{u.tokens30Days.toLocaleString()}</td>
                  <td>{u.approvals30Days}</td>
                  <td className="muted small">
                    {[u.ownVoice ? 'own voice' : null, u.personalSources ? `${u.personalSources} personal source(s)` : null].filter(Boolean).join(', ')}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </section>
  )
}
