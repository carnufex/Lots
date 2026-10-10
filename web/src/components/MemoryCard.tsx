import { useCallback, useEffect, useState } from 'react'
import type { Api } from '../api'
import { t } from '../i18n'

interface Memory {
  id: string
  text: string
  source: 'user' | 'agent'
  confirmed: boolean
  createdAt: string
}

/**
 * What Lots remembers about you (#99). Suggestions from conversations wait for your yes; only kept memories are used, as your own
 * notes. They tailor answers and never change what you or the agent may do.
 */
export default function MemoryCard({ api }: { api: Api }) {
  const [items, setItems] = useState<Memory[] | null>(null)
  const [text, setText] = useState('')
  const [editing, setEditing] = useState<{ id: string; text: string } | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    void api.raw<Memory[]>('/me/memories').then(setItems, () => setItems(null))
  }, [api])
  useEffect(load, [load])

  const act = async (f: () => Promise<unknown>) => {
    setError(null)
    try {
      await f()
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  if (!items) return null
  const suggestions = items.filter((m) => !m.confirmed)
  const kept = items.filter((m) => m.confirmed)

  return (
    <section className="card">
      <h2>{t('Memory')}</h2>
      <p className="muted small">
        {t('Facts and preferences Lots keeps about you, used as your own notes in every conversation. They never change what you or the agent may do. The agent can only suggest; you decide.')}
      </p>
      {suggestions.length > 0 && (
        <>
          <h3 className="small">{t('Suggested in conversations')}</h3>
          <ul className="memory-list">
            {suggestions.map((m) => (
              <li key={m.id}>
                <span>{m.text}</span>
                <span className="row">
                  <button type="button" className="btn small primary" onClick={() => void act(() => api.raw(`/me/memories/${m.id}/confirm`, { method: 'POST', body: '{}' }))}>
                    {t('Keep')}
                  </button>
                  <button type="button" className="btn small" onClick={() => void act(() => api.raw(`/me/memories/${m.id}`, { method: 'DELETE' }))}>
                    {t('Discard')}
                  </button>
                </span>
              </li>
            ))}
          </ul>
        </>
      )}
      <ul className="memory-list">
        {kept.map((m) => (
          <li key={m.id}>
            {editing?.id === m.id ? (
              <input value={editing.text} onChange={(e) => setEditing({ id: m.id, text: e.target.value })} aria-label={t('Edit memory')} maxLength={400} />
            ) : (
              <span>{m.text}</span>
            )}
            <span className="row">
              {editing?.id === m.id ? (
                <button type="button" className="btn small" onClick={() => void act(async () => {
                  await api.raw(`/me/memories/${m.id}`, { method: 'PUT', body: JSON.stringify({ text: editing.text }) })
                  setEditing(null)
                })}>
                  {t('Save')}
                </button>
              ) : (
                <button type="button" className="btn ghost small" onClick={() => setEditing({ id: m.id, text: m.text })}>
                  {t('Edit')}
                </button>
              )}
              <button type="button" className="btn ghost small" onClick={() => void act(() => api.raw(`/me/memories/${m.id}`, { method: 'DELETE' }))}>
                {t('Delete')}
              </button>
            </span>
          </li>
        ))}
      </ul>
      <form
        className="row"
        onSubmit={(e) => {
          e.preventDefault()
          if (!text.trim()) return
          void act(async () => {
            await api.raw('/me/memories', { method: 'POST', body: JSON.stringify({ text: text.trim() }) })
            setText('')
          })
        }}
      >
        <input className="grow" value={text} onChange={(e) => setText(e.target.value)} placeholder={t('e.g. I run Talos Linux at home')} maxLength={400} aria-label={t('New memory')} />
        <button type="submit" className="btn small" disabled={!text.trim()}>
          {t('Remember')}
        </button>
        {kept.length > 0 && (
          <button type="button" className="btn ghost small" onClick={() => window.confirm(t('Forget everything?')) && void act(() => api.raw('/me/memories', { method: 'DELETE' }))}>
            {t('Forget all')}
          </button>
        )}
      </form>
      {error && <p className="error small">{error}</p>}
    </section>
  )
}
