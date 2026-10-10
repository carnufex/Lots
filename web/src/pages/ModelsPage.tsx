import { useEffect, useMemo, useState } from 'react'
import { ApiError, type Api } from '../api'
import { adminApi, type ModelsInfo } from '../adminApi'
import { t } from '../i18n'

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
      .catch((e: unknown) => setError(e instanceof ApiError && e.status === 403 ? t('Models are visible to admins.') : String(e)))
  }
  useEffect(load, [admin])

  return (
    <section>
      <h1>{t('Models')}</h1>
      <p className="muted small">
        {t('Configured as code')} (<span className="mono">Models:Endpoints</span>, <span className="mono">Models:Aliases</span>).{' '}
        {t('An alias falls back to the next target when an endpoint is down, times out or answers 5xx.')}{' '}
        <button type="button" className="btn small" onClick={load}>
          {t('Check again')}
        </button>
      </p>
      {error && <p className="error">{error}</p>}
      {info && (
        <>
          <h2 className="section-title">{t('Endpoints')}</h2>
          <table>
            <thead>
              <tr>
                <th>{t('Endpoint')}</th>
                <th>{t('URL')}</th>
                <th>{t('Where')}</th>
                <th title={t('Highest data class this endpoint may see (#89)')}>{t('Cleared for')}</th>
                <th>{t('Health')}</th>
                <th>{t('Models served')}</th>
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
          <h2 className="section-title">{t('Aliases')}</h2>
          <table>
            <thead>
              <tr>
                <th>{t('Alias')}</th>
                <th>{t('Fallback chain')}</th>
                <th>{t('Fast reasoning effort')}</th>
                <th>{t('Used by')}</th>
              </tr>
            </thead>
            <tbody>
              {info.aliases.map((a) => (
                <tr key={a.name}>
                  <td className="mono">{a.name}</td>
                  <td className="mono small">{a.targets.map((it) => `${it.model}@${it.endpoint}`).join(' → ')}</td>
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
