import { useEffect, useState } from 'react'
import type { Api } from '../api'
import { fmt, t } from '../i18n'

type Coverage = { user: string; until: string; delegateTo: string | null }
type Away = { until: string | null; delegateTo: string | null }

/** Who is away and who covers (#136), and the caller's own out-of-office. Delegation routes notifications, never permissions. */
export default function AwayPanel({ api }: { api: Api }) {
  const [coverage, setCoverage] = useState<Coverage[]>([])
  const [mine, setMine] = useState<Away>({ until: null, delegateTo: null })
  const [until, setUntil] = useState('')
  const [delegate, setDelegate] = useState('')
  const [error, setError] = useState<string | null>(null)

  const load = () => {
    api.raw<Coverage[]>('/approvals/coverage').then(setCoverage).catch(() => setCoverage([]))
    api.raw<Away>('/me/away').then(setMine).catch(() => undefined)
  }
  useEffect(load, [api])

  const save = (body: Away) =>
    api
      .raw<Away>('/me/away', { method: 'PUT', body: JSON.stringify(body) })
      .then(() => {
        setError(null)
        load()
      })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))

  return (
    <div className="card small">
      {coverage.length > 0 && (
        <p>
          {t('Away:')}{' '}
          {coverage.map((c) => (
            <span key={c.user}>
              {t('{user} until {when}', { user: c.user, when: fmt.date(c.until) })}
              {c.delegateTo ? ` (${t('covered by {who}', { who: c.delegateTo })})` : ` (${t('no delegate')})`}{' '}
            </span>
          ))}
        </p>
      )}
      {mine.until ? (
        <p>
          {t('You are away until {when}', { when: fmt.dateTime(mine.until) })}
          {mine.delegateTo ? `; ${t('approval notifications go to {who}', { who: mine.delegateTo })}` : ''}.{' '}
          <button type="button" className="btn small" onClick={() => void save({ until: null, delegateTo: null })}>
            {t("I'm back")}
          </button>
        </p>
      ) : (
        <form
          className="filters"
          onSubmit={(e) => {
            e.preventDefault()
            void save({ until: new Date(until).toISOString(), delegateTo: delegate || null })
          }}
        >
          <label>
            {t('Away until')}
            <input type="datetime-local" value={until} onChange={(e) => setUntil(e.target.value)} required />
          </label>
          <label>
            {t('Delegate (user id)')}
            <input value={delegate} onChange={(e) => setDelegate(e.target.value)} />
          </label>
          <button className="btn" type="submit">
            {t('Set out of office')}
          </button>
          <span className="muted">{t('Your delegate can only decide what their own roles allow.')}</span>
        </form>
      )}
      {error && <p className="error">{error}</p>}
    </div>
  )
}
