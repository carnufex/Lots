import { useEffect, useState } from 'react'
import type { Api } from '../api'

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
      <h2 className="section-title">Stored data and retention</h2>
      {!r.audioStored && <p className="muted small">Conversation audio is not stored (no writable Speech:AudioPath).</p>}
      <table>
        <thead>
          <tr>
            <th>Data</th>
            <th>Rows</th>
            <th>Oldest</th>
            <th>Kept</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {r.tables.map((t) => (
            <tr key={t.name}>
              <td>{t.name}</td>
              <td>{t.rows.toLocaleString()}</td>
              <td className="muted">{t.oldest ? new Date(t.oldest).toLocaleDateString() : ''}</td>
              <td>{t.retentionDays > 0 ? `${t.retentionDays} days` : 'until deleted'}</td>
              <td className="muted small">{t.note}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  )
}
