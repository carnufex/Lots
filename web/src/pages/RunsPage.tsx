import { useEffect, useState } from 'react'
import type { Api, RunSummary } from '../api'
import { fmt, statusText, t } from '../i18n'

type Load = { state: 'loading' } | { state: 'error'; message: string } | { state: 'ok'; runs: RunSummary[] }

/**
 * Runs (#152): every execution of the agent loop the caller may see, including scheduled, API and webhook runs. A tab of History;
 * conversations are started from Chat.
 */
export function RunsList({ api }: { api: Api }) {
  const [load, setLoad] = useState<Load>({ state: 'loading' })

  useEffect(() => {
    let cancelled = false
    api
      .listRuns()
      .then((runs) => !cancelled && setLoad({ state: 'ok', runs }))
      .catch((e: unknown) => !cancelled && setLoad({ state: 'error', message: e instanceof Error ? e.message : String(e) }))
    return () => {
      cancelled = true
    }
  }, [api])

  return (
    <>
      <p className="muted small">{t('A run is one execution of the agent: a chat turn, a scheduled job, an API or webhook call. Conversations are made of runs.')}</p>
      {load.state === 'loading' && <p className="muted">{t('Loading…')}</p>}
      {load.state === 'error' && (
        <p role="alert" className="error">
          {t('Could not load runs:')} {load.message}
        </p>
      )}
      {load.state === 'ok' && load.runs.length === 0 && (
        <div className="empty">
          <p>{t('No runs yet.')}</p>
          <p className="muted">
            {t('Runs show up here with their status.')} <a href="#/chat">{t('Start a chat')}</a>
          </p>
        </div>
      )}
      {load.state === 'ok' && load.runs.length > 0 && (
        <table>
          <thead>
            <tr>
              <th>{t('Prompt')}</th>
              <th>{t('Context')}</th>
              <th>{t('User')}</th>
              <th>{t('Status')}</th>
              <th>{t('Started')}</th>
            </tr>
          </thead>
          <tbody>
            {load.runs.map((r) => (
              <tr key={r.id}>
                <td className="prompt">
                  <a href={`#/runs/${r.id}`}>{r.prompt}</a>
                </td>
                <td>{r.profile}</td>
                <td>{r.user}</td>
                <td>
                  <span className={`status ${r.status}`}>{statusText(r.status)}</span>
                </td>
                <td className="mono">{fmt.dateTime(r.createdAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  )
}
