import { useEffect, useMemo, useState } from 'react'
import { ApiError, type Api } from '../api'
import { adminApi, type PolicyMatrix, type ProfilePolicy, type Simulation } from '../adminApi'
import { t } from '../i18n'

const RISKS = ['Read', 'Write', 'Destructive']

/** Policy (#70): who may do what per profile, policy-test results, and a simulator that explains a decision. */
export default function PolicyPage({ api }: { api: Api }) {
  const admin = useMemo(() => adminApi(api), [api])
  const [policy, setPolicy] = useState<ProfilePolicy[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    admin
      .policy()
      .then(setPolicy)
      .catch((e: unknown) => setError(e instanceof ApiError && e.status === 403 ? t('Policy is visible to admins and auditors.') : String(e)))
  }, [admin])

  return (
    <section>
      <h1>{t('Policy')}</h1>
      <p className="muted small">{t('Evaluated outside the model on every tool call. Deny by default: a tool is callable only if its profile declares it and a role grants its risk class.')}</p>
      {error && <p className="error">{error}</p>}
      {policy && <Simulator api={api} policy={policy} />}
      {policy && policy.length > 0 && <Matrix api={api} policy={policy} />}
      {policy?.map((p) => (
        <div key={p.profile} className="card">
          <h2 className="section-title">
            {p.profile} <span className="muted small">v{p.version} · {p.managedBy}</span>
          </h2>
          <table>
            <thead>
              <tr>
                <th>{t('Role')}</th>
                {RISKS.map((r) => (
                  <th key={r}>{r}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {p.roles.map((r) => (
                <tr key={r.role}>
                  <td>{r.role}</td>
                  {RISKS.map((risk) => (
                    <td key={risk}>
                      {r.allow.includes(risk) ? (r.requireApproval.includes(risk) ? <span className="decision ApprovalRequested">{t('with approval')}</span> : <span className="decision Allowed">{t('allowed')}</span>) : <span className="muted">–</span>}
                      {r.approve.includes(risk) && <span className="muted small"> {t('· approves')}</span>}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
          <p className="small">
            Tools:{' '}
            {p.tools.map((it) => (
              <span key={it.name} className="mono">
                {it.name} ({it.risk}){' '}
              </span>
            ))}
          </p>
          {p.tests.length > 0 && (
            <p className="small">
              {t('Policy tests: {passed}/{total} passing', { passed: p.tests.filter((it) => it.passed).length, total: p.tests.length })}
              {p.tests
                .filter((it) => !it.passed)
                .map((it) => (
                  <span key={it.description} className="error">
                    {' '}
                    · {it.description}
                  </span>
                ))}
            </p>
          )}
        </div>
      ))}
    </section>
  )
}

function Simulator({ api, policy }: { api: Api; policy: ProfilePolicy[] }) {
  const admin = useMemo(() => adminApi(api), [api])
  const [profile, setProfile] = useState(policy[0]?.profile ?? '')
  const [roles, setRoles] = useState('operator')
  const [tool, setTool] = useState('')
  const [result, setResult] = useState<Simulation | null>(null)
  const tools = policy.find((p) => p.profile === profile)?.tools ?? []
  return (
    <div className="card">
      <h2 className="section-title">{t('Why would this be allowed?')}</h2>
      <form
        className="filters"
        onSubmit={(e) => {
          e.preventDefault()
          void admin.simulate(profile, roles.split(',').map((r) => r.trim()).filter(Boolean), tool).then(setResult)
        }}
      >
        <label>
          {t('Context')}
          <select value={profile} onChange={(e) => setProfile(e.target.value)}>
            {policy.map((p) => (
              <option key={p.profile}>{p.profile}</option>
            ))}
          </select>
        </label>
        <label>
          {t('Roles')}
          <input value={roles} onChange={(e) => setRoles(e.target.value)} />
        </label>
        <label>
          {t('Tool')}
          <input list="sim-tools" value={tool} onChange={(e) => setTool(e.target.value)} required />
          <datalist id="sim-tools">
            {tools.map((it) => (
              <option key={it.name} value={it.name} />
            ))}
          </datalist>
        </label>
        <button className="btn" type="submit">
          {t('Simulate')}
        </button>
      </form>
      {result && (
        <p>
          <span className={`decision ${result.decision === 'Allow' ? 'Allowed' : result.decision === 'Deny' ? 'Denied' : 'ApprovalRequested'}`}>{result.decision}</span>{' '}
          {result.rule ?? result.reason}
          <span className="muted small">
            {' '}
            · approvers: {result.approverRoles.join(', ') || 'none'} · these roles see: {result.visibleTools.join(', ') || 'no tools'}
          </span>
        </p>
      )}
    </div>
  )
}

const CELL: Record<string, { cls: string; label: string }> = {
  Allow: { cls: 'Allowed', label: 'allowed' },
  RequireApproval: { cls: 'ApprovalRequested', label: 'with approval' },
  Deny: { cls: 'Denied', label: 'denied' },
}

/** Roles × tools (#156): the policy engine's own decision for each cell, the reason on hover. */
function Matrix({ api, policy }: { api: Api; policy: ProfilePolicy[] }) {
  const admin = useMemo(() => adminApi(api), [api])
  const [profile, setProfile] = useState(policy[0].profile)
  const [m, setM] = useState<PolicyMatrix | null>(null)
  useEffect(() => {

    admin.matrix(profile).then(setM).catch(() => setM(null))
  }, [admin, profile])
  return (
    <div className="card">
      <h2 className="section-title">{t('Who may call what')}</h2>
      <label className="small">
        {t('Context')}{' '}
        <select value={profile} onChange={(e) => setProfile(e.target.value)}>
          {policy.map((p) => (
            <option key={p.profile}>{p.profile}</option>
          ))}
        </select>
      </label>
      {m && (
        <div className="table-scroll">
          <table className="matrix">
            <thead>
              <tr>
                <th>{t('Tool')}</th>
                {m.roles.map((r) => (
                  <th key={r}>{r}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {m.tools.map((tool) => (
                <tr key={tool.name}>
                  <td>
                    <span className="mono">{tool.name}</span> <span className="muted small">{tool.risk}</span>
                  </td>
                  {m.roles.map((r) => {
                    const c = m.cells[r][tool.name]
                    const look = CELL[c.decision]
                    return (
                      <td key={r} title={c.reason}>
                        <span className={`decision ${look.cls}`}>{t(look.label)}</span>
                      </td>
                    )
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
