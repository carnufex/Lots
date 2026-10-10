import { useEffect, useState } from 'react'
import type { Api } from '../api'

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
          Away:{' '}
          {coverage.map((c) => (
            <span key={c.user}>
              {c.user} until {new Date(c.until).toLocaleDateString()}
              {c.delegateTo ? ` (covered by ${c.delegateTo})` : ' (no delegate)'}{' '}
            </span>
          ))}
        </p>
      )}
      {mine.until ? (
        <p>
          You are away until {new Date(mine.until).toLocaleString()}
          {mine.delegateTo ? `; approval notifications go to ${mine.delegateTo}` : ''}.{' '}
          <button type="button" className="btn small" onClick={() => void save({ until: null, delegateTo: null })}>
            I'm back
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
            Away until
            <input type="datetime-local" value={until} onChange={(e) => setUntil(e.target.value)} required />
          </label>
          <label>
            Delegate (user id)
            <input value={delegate} onChange={(e) => setDelegate(e.target.value)} />
          </label>
          <button className="btn" type="submit">
            Set out of office
          </button>
          <span className="muted">Your delegate can only decide what their own roles allow.</span>
        </form>
      )}
      {error && <p className="error">{error}</p>}
    </div>
  )
}
