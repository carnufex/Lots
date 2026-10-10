import { useEffect, useState } from 'react'
import type { Api, RunSummary } from '../api'
import type { ProfileInfo, VoiceConfig } from '../config'
import NewRun from '../components/NewRun'
import Conversation from '../components/Conversation'
import { fmt, statusText, t } from '../i18n'

type Load = { state: 'loading' } | { state: 'error'; message: string } | { state: 'ok'; runs: RunSummary[] }

export default function RunsPage({ api, profiles, voice }: { api: Api; profiles: ProfileInfo[]; voice: VoiceConfig }) {
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
    <section>
      <h1>{t('Runs')}</h1>
      {voice.enabled && <Conversation api={api} profiles={profiles} defaultLanguage={voice.defaultLanguage} />}
      <NewRun api={api} profiles={profiles} voice={voice} />
      {load.state === 'loading' && <p className="muted">{t('Loading…')}</p>}
      {load.state === 'error' && (
        <p role="alert" className="error">
          {t('Could not load runs:')} {load.message}
        </p>
      )}
      {load.state === 'ok' && load.runs.length === 0 && (
        <div className="empty">
          <p>{t('No runs yet.')}</p>
          <p className="muted">{t('Runs you start will show up here with their status.')}</p>
        </div>
      )}
      {load.state === 'ok' && load.runs.length > 0 && (
        <table>
          <thead>
            <tr>
              <th>{t('Prompt')}</th>
              <th>{t('Profile')}</th>
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
    </section>
  )
}
