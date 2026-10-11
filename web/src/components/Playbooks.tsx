import { useEffect, useState } from 'react'
import type { Api, Playbook, PlaybookProgress } from '../api'
import { t } from '../i18n'

/** Playbooks (#161, ADR 0022): declared step-by-step flows the shell enforces. Read-only here; applied as config. */
export function PlaybookList({ api }: { api: Api }) {
  const [list, setList] = useState<Playbook[] | null>(null)
  useEffect(() => void api.playbooks().then(setList, () => setList([])), [api])
  if (!list || list.length === 0) return null
  return (
    <div className="card">
      <h2 className="section-title">{t('Playbooks')}</h2>
      <p className="muted small">{t('Step-by-step flows the shell enforces: only the current step’s tools, approvals where a step needs them, no skipping. Applied as config (kind: Playbook).')}</p>
      {list.map((p) => (
        <details key={p.name}>
          <summary>
            <strong>{p.name}</strong> <span className="muted small">v{p.version} · {p.profile} · {p.managedBy}</span> — {p.description}
          </summary>
          <ol className="small">
            {p.steps.map((s) => (
              <li key={s.name}>
                <strong>{s.name}</strong>: {s.instruction} <span className="muted mono">[{s.tools.join(', ')}]</span>
                {s.requireApproval && <span className="decision ApprovalRequested"> {t('approval')}</span>}
                {s.check && <span className="muted"> · {t('done when')} {s.check.tool}{s.check.contains ? ` ${t('contains')} "${s.check.contains}"` : ''}</span>}
              </li>
            ))}
          </ol>
          {p.spec && <pre className="mono small">{p.spec}</pre>}
        </details>
      ))}
    </div>
  )
}

/** Where a playbook run is: done steps, the current one, the rest. */
export function PlaybookSteps({ progress }: { progress: PlaybookProgress }) {
  return (
    <ol className="playbook-steps small" aria-label={t('Playbook {name}', { name: progress.name })}>
      {progress.steps.map((s) => {
        const state = progress.completed.includes(s) ? 'done' : s === progress.current ? 'current' : 'todo'
        return (
          <li key={s} className={state} aria-current={state === 'current' ? 'step' : undefined}>
            {state === 'done' ? '✓ ' : ''}{s}
          </li>
        )
      })}
    </ol>
  )
}
