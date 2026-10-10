import { useEffect, useMemo, useState } from 'react'
import { ApiError, type Api } from '../api'
import DataReport from '../components/DataReport'
import { adminApi, type IdentityInfo, type MappingTest, type UserInfo } from '../adminApi'
import { fmt, t } from '../i18n'

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
      .catch((e: unknown) => setError(e instanceof ApiError && e.status === 403 ? t('Identity settings are visible to admins.') : String(e)))
    admin.users().then(setUsers).catch(() => setUsers([]))
  }, [admin])

  return (
    <section>
      <h1>{t('Identity')}</h1>
      {error && <p className="error">{error}</p>}
      {info && (
        <>
          <div className="card">
            <dl className="meta">
              <div>
                <dt>{t('Login')}</dt>
                <dd>{info.mode === 'Oidc' ? 'OIDC' : <span className="warn">{t('Dev (local development only)')}</span>}</dd>
              </div>
              {info.authority && (
                <div>
                  <dt>{t('Authority')}</dt>
                  <dd className="mono small">{info.authority}</dd>
                </div>
              )}
              <div>
                <dt>{t('Client')}</dt>
                <dd>{info.clientId ?? '–'}</dd>
              </div>
              <div>
                <dt>{t('User claim')}</dt>
                <dd className="mono">{info.userClaim}</dd>
              </div>
              <div>
                <dt>{t('Role claim')}</dt>
                <dd className="mono">
                  {info.roleClaim}
                  {info.rolePrefix ? ` (prefix ${info.rolePrefix})` : ''}
                </dd>
              </div>
              <div>
                <dt>{t('Admins / auditors')}</dt>
                <dd>
                  {info.adminRoles} / {info.auditRoles}
                </dd>
              </div>
              <div>
                <dt>{t('Roles in profiles')}</dt>
                <dd>{info.knownRoles.join(', ')}</dd>
              </div>
            </dl>
            {info.devHeaders && <p className="warn small">{t('Dev identity headers are enabled: anyone can act as anyone. Never outside local development.')}</p>}
          </div>

          <h2 className="section-title">{t('How backends are reached')}</h2>
          <p className="muted small">{t('Only admins see this; end users never see which account a call used.')}</p>
          <table>
            <thead>
              <tr>
                <th>{t('Server')}</th>
                <th>{t('Profiles')}</th>
                <th>{t('Identity')}</th>
                <th>{t('Credentials')}</th>
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

          <h2 className="section-title">{t('Test a login mapping')}</h2>
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
              {t('Map')}
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
                · {test.issuer} · expires {test.expiresAt ? fmt.dateTime(test.expiresAt) : '–'} · {test.note}
              </span>
            </p>
          )}
        </>
      )}

      <DataReport api={api} />
      {users.length > 0 && (
        <>
          <h2 className="section-title">{t('Users')}</h2>
          <p className="muted small">{t("People who used Lots. Accounts and groups live in the identity provider; roles shown are from each user's latest run.")}</p>
          <table>
            <thead>
              <tr>
                <th>{t('User')}</th>
                <th>{t('Roles')}</th>
                <th>{t('Last active')}</th>
                <th>{t('Runs (30 d)')}</th>
                <th>{t('Tokens (30 d)')}</th>
                <th>{t('Approvals (30 d)')}</th>
                <th>{t('Personal data')}</th>
              </tr>
            </thead>
            <tbody>
              {users.map((u) => (
                <tr key={u.user}>
                  <td>{u.user}</td>
                  <td>{u.roles.join(', ')}</td>
                  <td className="muted">{fmt.dateTime(u.lastActive)}</td>
                  <td>
                    {u.runs30Days} <span className="muted small">/ {u.runs}</span>
                  </td>
                  <td>{fmt.number(u.tokens30Days)}</td>
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
