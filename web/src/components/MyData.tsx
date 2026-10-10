import { useState } from 'react'
import type { Api } from '../api'
import type { ProfileInfo } from '../config'
import { t } from '../i18n'

/** Your data (#79): download everything Lots keeps about you, or delete it. */
const MODES: Record<string, string> = {
  off: 'nothing about the content, only that the run happened',
  metadata: 'timings, tools used and outcomes, but none of your words or the answers',
  redacted: 'also your questions, the answers and tool results, with personal data and secrets masked',
  full: 'also your questions, the answers and tool results, with secrets masked',
}

/** Your data (#79): download everything Lots keeps about you, or delete it. And what telemetry records of your runs (#145). */
export default function MyData({ api, profiles = [] }: { api: Api; profiles?: ProfileInfo[] }) {
  const [message, setMessage] = useState<string | null>(null)

  const download = async () => {
    const res = await fetch('/me/export', { headers: await api.authHeaders() })
    if (!res.ok) return setMessage(`Export failed (${res.status}).`)
    const url = URL.createObjectURL(await res.blob())
    const a = document.createElement('a')
    a.href = url
    a.download = `lots-export-${new Date().toISOString().slice(0, 10)}.zip`
    a.click()
    URL.revokeObjectURL(url)
  }

  const remove = async () => {
    const typed = window.prompt(t('This deletes your runs, conversations and recordings, settings, own voice, vocabulary, personal knowledge and connected accounts. The audit log is kept. Type "delete my data" to confirm.'))
    if (typed !== 'delete my data') return
    try {
      const r = await api.raw<{ deleted: Record<string, number> }>('/me/data', { method: 'DELETE', body: JSON.stringify({ confirm: typed }) })
      setMessage(t('Deleted: ') + Object.entries(r.deleted).map(([k, v]) => `${k} ${v}`).join(', '))
    } catch (e) {
      setMessage(String(e))
    }
  }

  return (
    <div className="card small">
      <strong>{t('Your data')}</strong>{' '}
      <button type="button" className="btn small" onClick={() => void download()}>
        {t('Download everything (zip)')}
      </button>{' '}
      <button type="button" className="btn small" onClick={() => void remove()}>
        {t('Delete my data')}
      </button>
      {message && <p className="muted">{message}</p>}
      {profiles.length > 0 && (
        <details className="capture">
          <summary>{t('What monitoring records about your runs')}</summary>
          <p className="muted">
            {t("Besides your own history, Lots sends monitoring data to the operators' observability tools. Per context:")}
          </p>
          <ul>
            {profiles.map((p) => (
              <li key={p.name}>
                <strong>{p.name}</strong>: {t(MODES[p.telemetryContent ?? 'metadata'])}
              </li>
            ))}
          </ul>
          <p className="muted">{t('You appear there only as an anonymous code, never by name. Deleting your data removes your runs here; monitoring copies expire with their own retention (about a week).')}</p>
        </details>
      )}
    </div>
  )
}
