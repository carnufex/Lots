import { useEffect, useState } from 'react'
import type { Api } from '../api'
import { fmt, t } from '../i18n'

type Report = { tables: { name: string; rows: number; oldest: string | null; retentionDays: number; note: string }[]; audioRetentionDays: number; audioStored: boolean }

/** What is stored, how much, how old and how long it is kept (#79). Admins. */
export default function DataReport({ api }: { api: Api }) {
  const [r, setR] = useState<Report | null>(null)
  useEffect(() => {
    api.raw<Report>('/admin/v1/data-report').then(setR).catch(() => setR(null))
  }, [api])
  if (!r) return null
  return (
    <>
      <h2 className="section-title">{t('Stored data and retention')}</h2>
      {!r.audioStored && <p className="muted small">{t('Conversation audio is not stored (no writable Speech:AudioPath).')}</p>}
      <table>
        <thead>
          <tr>
            <th>{t('Data')}</th>
            <th>{t('Rows')}</th>
            <th>{t('Oldest')}</th>
            <th>{t('Kept')}</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {r.tables.map((it) => (
            <tr key={it.name}>
              <td>{it.name}</td>
              <td>{fmt.number(it.rows)}</td>
              <td className="muted">{it.oldest ? fmt.date(it.oldest) : ''}</td>
              <td>{it.retentionDays > 0 ? `${it.retentionDays} days` : 'until deleted'}</td>
              <td className="muted small">{it.note}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  )
}
