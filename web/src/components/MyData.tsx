import { useState } from 'react'
import type { Api } from '../api'

/** Your data (#79): download everything Lots keeps about you, or delete it. */
export default function MyData({ api }: { api: Api }) {
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
    const typed = window.prompt('This deletes your runs, conversations and recordings, settings, own voice, vocabulary, personal knowledge and connected accounts. The audit log is kept. Type "delete my data" to confirm.')
    if (typed !== 'delete my data') return
    try {
      const r = await api.raw<{ deleted: Record<string, number> }>('/me/data', { method: 'DELETE', body: JSON.stringify({ confirm: typed }) })
      setMessage('Deleted: ' + Object.entries(r.deleted).map(([k, v]) => `${k} ${v}`).join(', '))
    } catch (e) {
      setMessage(String(e))
    }
  }

  return (
    <div className="card small">
      <strong>Your data</strong>{' '}
      <button type="button" className="btn small" onClick={() => void download()}>
        Download everything (zip)
      </button>{' '}
      <button type="button" className="btn small" onClick={() => void remove()}>
        Delete my data
      </button>
      {message && <p className="muted">{message}</p>}
    </div>
  )
}
