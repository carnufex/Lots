import { useEffect, useState } from 'react'
import { ApiError, type Api, type Approval } from '../api'
import AwayPanel from '../components/Away'
import { fmt, t } from '../i18n'

const POLL_MS = 5000

export default function ApprovalsPage({ api }: { api: Api }) {
  const [items, setItems] = useState<Approval[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [reload, setReload] = useState(0)

  useEffect(() => {
    let cancelled = false
    const load = () =>
      api
        .listApprovals()
        .then((list) => {
          if (cancelled) return
          setItems(list)
          setError(null)
        })
        .catch((e: unknown) => !cancelled && setError(e instanceof Error ? e.message : String(e)))
    void load()
    const timer = window.setInterval(() => void load(), POLL_MS)
    return () => {
      cancelled = true
      window.clearInterval(timer)
    }
  }, [api, reload])

  return (
    <section>
      <h1>{t('Approvals')}</h1>
      <AwayPanel api={api} />
      {error && (
        <p role="alert" className="error">
          {t('Could not load approvals:')} {error}
        </p>
      )}
      {!items && !error && <p className="muted">{t('Loading…')}</p>}
      {items && items.length === 0 && (
        <div className="empty">
          <p>{t('Nothing waiting for you.')}</p>
          <p className="muted">{t('Calls that need your approval appear here and pause their run until you decide.')}</p>
        </div>
      )}
      {items && items.length > 0 && (
        <ul className="cards">
          {items.map((a) => (
            <ApprovalCard key={a.id} approval={a} api={api} onDecided={() => setReload((n) => n + 1)} />
          ))}
        </ul>
      )}
    </section>
  )
}

function ApprovalCard({ approval, api, onDecided }: { approval: Approval; api: Api; onDecided: () => void }) {
  const [comment, setComment] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const decide = async (outcome: 'approve' | 'deny') => {
    setBusy(true)
    setError(null)
    try {
      await api.decide(approval.id, outcome, comment.trim())
      onDecided()
    } catch (e) {
      setBusy(false)
      setError(
        e instanceof ApiError && e.status === 403
          ? t('You are not allowed to decide this.')
          : e instanceof ApiError && e.status === 409
            ? t('Someone else already decided this.')
            : e instanceof Error
              ? e.message
              : String(e),
      )
      if (e instanceof ApiError && e.status === 409) onDecided()
    }
  }

  return (
    <li className="card">
      <div className="cardhead">
        <span className="name">{approval.tool}</span>
        <span className="muted">
          {t('requested by')} <strong>{approval.requestedBy}</strong> · {fmt.dateTime(approval.requestedAt)}
        </span>
        <a href={`#/runs/${approval.runId}`}>{t('View run')}</a>
      </div>
      <p className="small muted">
        {approval.risk && <span className={`decision ${approval.risk === 'Destructive' ? 'Denied' : 'ApprovalRequested'}`}>{t(approval.risk)}</span>}
        {approval.requiredApprovals > 1 && (
          <span>
            {' '}
            · {t('two approvers needed')}{approval.approvedBy.length > 0 ? `, ${t('approved so far by {who}', { who: approval.approvedBy.join(', ') })}` : ''}
          </span>
        )}
        {approval.expiresAt && <span> · {t('expires {when} (then refused)', { when: fmt.dateTime(approval.expiresAt) })}</span>}
      </p>
      {approval.arguments && <pre>{pretty(approval.arguments)}</pre>}
      <div className="row approval-actions">
        <input
          className="wide"
          placeholder={approval.commentRequired ? t('Reason (required)') : t('Comment (optional)')}
          required={approval.commentRequired}
          aria-label={t('Comment')}
          value={comment}
          onChange={(e) => setComment(e.target.value)}
        />
        {error && (
          <span role="alert" className="error">
            {error}
          </span>
        )}
        <button className="btn" disabled={busy || (approval.commentRequired && !comment.trim())} onClick={() => void decide('deny')}>
          {t('Deny')}
        </button>
        <button className="btn primary" disabled={busy || (approval.commentRequired && !comment.trim())} onClick={() => void decide('approve')}>
          {t('Approve')}
        </button>
      </div>
    </li>
  )
}

function pretty(json: string): string {
  try {
    return JSON.stringify(JSON.parse(json), null, 2)
  } catch {
    return json
  }
}
