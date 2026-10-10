import { useEffect, useMemo, useState } from 'react'
import { ApiError, type Api } from '../api'
import { adminApi, type ModelsInfo } from '../adminApi'

/** Models (#71): OpenAI-compatible endpoints with live health, and the aliases (fallback chains) profiles and features use. */
export default function ModelsPage({ api }: { api: Api }) {
  const admin = useMemo(() => adminApi(api), [api])
  const [info, setInfo] = useState<ModelsInfo | null>(null)
  const [error, setError] = useState<string | null>(null)
  const load = () => {
    setError(null)
    admin
      .models()
      .then(setInfo)
      .catch((e: unknown) => setError(e instanceof ApiError && e.status === 403 ? 'Models are visible to admins.' : String(e)))
  }
  useEffect(load, [admin])

  return (
    <section>
      <h1>Models</h1>
      <p className="muted small">
        Configured as code (<span className="mono">Models:Endpoints</span>, <span className="mono">Models:Aliases</span>). An alias falls back to the next
        target when an endpoint is down, times out or answers 5xx.{' '}
        <button type="button" className="btn small" onClick={load}>
          Check again
        </button>
      </p>
      {error && <p className="error">{error}</p>}
      {info && (
        <>
          <h2 className="section-title">Endpoints</h2>
          <table>
            <thead>
              <tr>
                <th>Endpoint</th>
                <th>URL</th>
                <th>Where</th>
                <th title="Highest data class this endpoint may see (#89)">Cleared for</th>
                <th>Health</th>
                <th>Models served</th>
              </tr>
            </thead>
            <tbody>
              {info.endpoints.map((e) => (
                <tr key={e.name}>
                  <td>{e.name}</td>
                  <td className="mono small">{e.baseUrl}</td>
                  <td>{e.location}</td>
                  <td>{e.clearance ?? ''}</td>
                  <td title={e.error ?? undefined}>
                    <span className={`decision ${e.up ? 'Allowed' : 'Denied'}`}>{e.up ? `up · ${e.latencyMs} ms` : 'down'}</span>
                  </td>
                  <td className="muted small">{e.models.join(', ')}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <h2 className="section-title">Aliases</h2>
          <table>
            <thead>
              <tr>
                <th>Alias</th>
                <th>Fallback chain</th>
                <th>Fast reasoning effort</th>
                <th>Used by</th>
              </tr>
            </thead>
            <tbody>
              {info.aliases.map((a) => (
                <tr key={a.name}>
                  <td className="mono">{a.name}</td>
                  <td className="mono small">{a.targets.map((t) => `${t.model}@${t.endpoint}`).join(' → ')}</td>
                  <td>{a.fastReasoningEffort ?? ''}</td>
                  <td className="muted small">{a.usedBy.join(', ') || (a.name === 'embed' ? 'knowledge search' : a.name === 'judge' ? 'conflict checks' : '')}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </section>
  )
}
