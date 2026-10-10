import { useCallback, useEffect, useMemo, useState } from 'react'
import { ApiError, type Api } from '../api'
import { adminApi, type ApplyOutcome, type GitOpsInfo, type Resource, type ResourceVersion } from '../adminApi'

const NEW_PROFILE = `kind: Profile
name: my-profile
version: 1
description: What this profile is for.
instructions: |
  How the agent should work in this domain.
servers: []
tools: []
roles:
  - name: operator
    allow: [read]
`

/** Diff text with +/- lines coloured. Spec text is configuration, rendered as text only. */
export function DiffView({ diff }: { diff: string }) {
  return (
    <pre className="diff">
      {diff.split('\n').map((line, i) => (
        <span key={i} className={line.startsWith('+ ') ? 'add' : line.startsWith('- ') ? 'del' : undefined}>
          {line}
          {'\n'}
        </span>
      ))}
    </pre>
  )
}

function Outcome({ outcome }: { outcome: ApplyOutcome }) {
  return (
    <div className="outcome">
      <p className={outcome.hasErrors ? 'error' : 'muted'}>
        {outcome.hasErrors ? 'Not applied: fix the errors below.' : outcome.applied ? 'Applied.' : 'Dry run: nothing changed yet.'}
      </p>
      {outcome.results.map((r) => (
        <div key={`${r.kind}:${r.name}`}>
          <strong>
            {r.action} {r.kind} {r.name}
          </strong>{' '}
          {r.version > 0 && <span className="muted">v{r.version}</span>}
          {r.errors.map((e) => (
            <p key={e} className="error small">
              {e}
            </p>
          ))}
          {r.diff && r.action !== 'unchanged' && <DiffView diff={r.diff} />}
        </div>
      ))}
    </div>
  )
}

/** Profiles (#69): view any profile, its version history with diffs; edit and apply the ones not managed as code. GitOps drift (#68). */
export default function ProfilesPage({ api }: { api: Api }) {
  const admin = useMemo(() => adminApi(api), [api])
  const [resources, setResources] = useState<Resource[] | null>(null)
  const [gitops, setGitops] = useState<GitOpsInfo | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [editing, setEditing] = useState<string | null>(null)

  const load = useCallback(() => {
    admin
      .resources()
      .then((r) => setResources(r.filter((x) => x.kind === 'Profile')))
      .catch((e: unknown) => setError(e instanceof ApiError && e.status === 403 ? 'Profiles are managed by admins.' : String(e)))
    admin.gitops().then(setGitops).catch(() => setGitops(null))
  }, [admin])
  useEffect(load, [load])

  return (
    <section>
      <h1>Profiles</h1>
      {error && <p className="error">{error}</p>}
      {gitops?.state.enabled && (
        <div className="card">
          <strong>GitOps</strong>{' '}
          <span className="muted small">
            {gitops.state.path} · revision {gitops.state.revision ?? 'unknown'} · last sync{' '}
            {gitops.state.lastSyncAt ? new Date(gitops.state.lastSyncAt).toLocaleString() : 'never'}: {gitops.state.lastResult}
          </span>{' '}
          <button type="button" className="btn small" onClick={() => void admin.syncGitops().then(load)}>
            Sync now
          </button>
          {gitops.state.error && <p className="error small">{gitops.state.error}</p>}
          {gitops.drift.length === 0 ? (
            <p className="muted small">In sync with Git.</p>
          ) : (
            <>
              <p className="warn small">Differs from Git ({gitops.drift.length}): the next sync applies this.</p>
              {gitops.drift.map((d) => (
                <details key={`${d.kind}:${d.name}`}>
                  <summary>
                    {d.action} {d.kind} {d.name}
                  </summary>
                  {d.diff && <DiffView diff={d.diff} />}
                </details>
              ))}
            </>
          )}
        </div>
      )}

      {resources && (
        <table>
          <thead>
            <tr>
              <th>Profile</th>
              <th>Version</th>
              <th>Managed by</th>
              <th>Last applied</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {resources.map((r) => (
              <tr key={r.name}>
                <td>
                  <button type="button" className="btn ghost" onClick={() => setSelected(selected === r.name ? null : r.name)} aria-expanded={selected === r.name}>
                    {r.name}
                  </button>
                </td>
                <td>{r.version}</td>
                <td>
                  {r.managedBy}
                  {!r.editable && <span className="muted small"> (read-only)</span>}
                </td>
                <td className="muted">{r.appliedAt ? `${new Date(r.appliedAt).toLocaleString()} by ${r.appliedBy}` : ''}</td>
                <td>
                  {r.editable && (
                    <button type="button" className="btn small" onClick={() => void admin.resource('Profile', r.name).then((x) => setEditing(x.spec))}>
                      Edit
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {selected && <ProfileDetail api={api} name={selected} />}

      <p>
        <button type="button" className="btn" onClick={() => setEditing(NEW_PROFILE)}>
          New profile
        </button>
      </p>
      {editing !== null && (
        <Editor
          api={api}
          initial={editing}
          onDone={() => {
            setEditing(null)
            load()
          }}
        />
      )}
      <Schedules api={api} />
    </section>
  )
}

interface Schedule {
  name: string
  profile: string
  user: string
  roles: string[]
  cron: string | null
  timeZone: string
  next: string | null
  webhook: boolean
  enabled: boolean
  viewers: string[]
  deliverTargets: number
  lastRunId: string | null
  lastStatus: string | null
  lastRunAt: string | null
}

/** Scheduled and triggered runs (#101): applied like profiles (kind: Schedule), listed here with their next run. */
function Schedules({ api }: { api: Api }) {
  const [list, setList] = useState<Schedule[] | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const load = useCallback(() => {
    api.raw<Schedule[]>('/admin/schedules').then(setList).catch(() => setList(null))
  }, [api])
  useEffect(load, [load])
  if (!list) return null

  const runNow = async (name: string) => {
    try {
      const r = await api.raw<{ runId: string }>(`/admin/schedules/${encodeURIComponent(name)}/run`, { method: 'POST', body: '{}' })
      setMessage(null)
      window.location.assign(`#/runs/${r.runId}`)
    } catch (e) {
      setMessage(String(e))
    }
  }

  return (
    <>
      <h2 className="section-title">Schedules</h2>
      <p className="muted small">
        Runs without a person: on a cron schedule or from a webhook, as a service identity with its own roles. Apply them like
        profiles (<code>kind: Schedule</code>); see docs/schedules.md.
      </p>
      {list.length === 0 ? (
        <p className="muted small">No schedules applied.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>Runs as</th>
              <th>When</th>
              <th>Next</th>
              <th>Last run</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {list.map((s) => (
              <tr key={s.name}>
                <td>
                  {s.name}
                  {!s.enabled && <span className="muted small"> disabled</span>}
                  <div className="muted small">{s.profile}</div>
                </td>
                <td className="small">
                  {s.user}
                  <div className="muted">{s.roles.join(', ')}</div>
                </td>
                <td className="mono small">
                  {s.cron ?? ''}
                  {s.cron && s.timeZone !== 'UTC' && <div className="muted">{s.timeZone}</div>}
                  {s.webhook && <div className="muted">webhook</div>}
                </td>
                <td className="small">{s.next ? new Date(s.next).toLocaleString() : ''}</td>
                <td className="small">
                  {s.lastRunId ? (
                    <a href={`#/runs/${s.lastRunId}`}>
                      {s.lastStatus} · {new Date(s.lastRunAt!).toLocaleString()}
                    </a>
                  ) : (
                    <span className="muted">never</span>
                  )}
                </td>
                <td>
                  <button type="button" className="btn small" onClick={() => void runNow(s.name)}>
                    Run now
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {message && <p className="error small">{message}</p>}
    </>
  )
}

function ProfileDetail({ api, name }: { api: Api; name: string }) {
  const admin = useMemo(() => adminApi(api), [api])
  const [spec, setSpec] = useState<string | null>(null)
  const [versions, setVersions] = useState<ResourceVersion[]>([])
  useEffect(() => {
    admin.resource('Profile', name).then((r) => setSpec(r.spec)).catch(() => setSpec(null))
    admin.versions('Profile', name).then(setVersions).catch(() => setVersions([]))
  }, [admin, name])
  return (
    <div className="card">
      <h2 className="section-title">{name}</h2>
      {spec !== null && <pre>{spec}</pre>}
      {versions.length > 0 && (
        <>
          <h3 className="section-title">History</h3>
          {versions.map((v) => (
            <details key={v.id}>
              <summary>
                v{v.version} {v.action} by {v.appliedBy} ({v.managedBy}) · {new Date(v.appliedAt).toLocaleString()}
              </summary>
              <DiffView diff={v.diff} />
            </details>
          ))}
        </>
      )}
    </div>
  )
}

/** YAML editor with a dry run (validation + diff) before apply. The version must be raised for a change; Bump does that. */
function Editor({ api, initial, onDone }: { api: Api; initial: string; onDone: () => void }) {
  const admin = useMemo(() => adminApi(api), [api])
  const [yaml, setYaml] = useState(initial)
  const [outcome, setOutcome] = useState<ApplyOutcome | null>(null)
  const [busy, setBusy] = useState(false)
  const run = async (dryRun: boolean) => {
    setBusy(true)
    try {
      const o = await admin.apply(yaml, dryRun)
      setOutcome(o)
      if (o.applied) onDone()
    } finally {
      setBusy(false)
    }
  }
  const bump = () => setYaml(yaml.replace(/^version:\s*(\d+)/m, (_, v: string) => `version: ${Number(v) + 1}`))
  return (
    <div className="card">
      <h2 className="section-title">Edit</h2>
      <textarea className="code" rows={22} value={yaml} onChange={(e) => setYaml(e.target.value)} spellCheck={false} aria-label="Profile YAML" />
      <p>
        <button type="button" className="btn" disabled={busy} onClick={bump}>
          Bump version
        </button>{' '}
        <button type="button" className="btn" disabled={busy} onClick={() => void run(true)}>
          Validate and diff
        </button>{' '}
        <button type="button" className="btn primary" disabled={busy || !outcome || outcome.hasErrors || !outcome.dryRun} onClick={() => void run(false)}>
          Apply
        </button>{' '}
        <button type="button" className="btn ghost" onClick={onDone}>
          Cancel
        </button>
      </p>
      {outcome && <Outcome outcome={outcome} />}
    </div>
  )
}
