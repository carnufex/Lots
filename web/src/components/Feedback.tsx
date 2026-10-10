import { useEffect, useState } from 'react'
import type { Api } from '../api'
import { t } from '../i18n'

/**
 * Thumbs up or down on an answer, with an optional comment (#121). A thumbs down opens the comment at once: what was wrong is
 * what makes the rating useful. Clicking the active thumb again removes the rating.
 */
export default function Feedback({ api, runId, rating: initialRating, comment: initialComment, load = false }: {
  api: Api
  runId: string
  rating?: number | null
  comment?: string | null
  /** Fetch the user's rating (the run page has none in hand). */
  load?: boolean
}) {
  const [rating, setRating] = useState<number | null>(initialRating ?? null)
  const [comment, setComment] = useState(initialComment ?? '')
  const [saved, setSaved] = useState(initialComment ?? '')
  const [open, setOpen] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (!load) return
    let cancelled = false
    api.myFeedback(runId).then((f) => {
      if (cancelled || !f) return
      setRating(f.rating)
      setComment(f.comment ?? '')
      setSaved(f.comment ?? '')
    }).catch(() => undefined)
    return () => {
      cancelled = true
    }
  }, [api, runId, load])

  const send = async (next: 1 | -1 | null, text = comment) => {
    setBusy(true)
    setError(null)
    try {
      if (next === null) {
        await api.unrate(runId)
        setComment('')
        setSaved('')
        setOpen(false)
      } else {
        const f = await api.rate(runId, next, text)
        setSaved(f.comment ?? '')
      }
      setRating(next)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const thumb = (value: 1 | -1) => {
    if (rating === value) return void send(null)
    void send(value)
    setOpen(value === -1 || open)
  }

  return (
    <span className="feedback">
      <button type="button" className={`btn ghost small thumb ${rating === 1 ? 'on' : ''}`} aria-pressed={rating === 1} disabled={busy}
        aria-label={t('Good answer')} title={t('Good answer')} onClick={() => thumb(1)}>
        👍
      </button>
      <button type="button" className={`btn ghost small thumb ${rating === -1 ? 'on' : ''}`} aria-pressed={rating === -1} disabled={busy}
        aria-label={t('Bad answer')} title={t('Bad answer')} onClick={() => thumb(-1)}>
        👎
      </button>
      {rating !== null && !open && (
        <button type="button" className="btn ghost small" onClick={() => setOpen(true)}>
          {saved ? t('Edit comment') : t('Add a comment')}
        </button>
      )}
      {open && rating !== null && (
        <form className="feedback-form" onSubmit={(e) => {
          e.preventDefault()
          void send(rating as 1 | -1).then(() => setOpen(false))
        }}>
          <label className="sr" htmlFor={`fb-${runId}`}>{t('What was good or wrong?')}</label>
          <textarea id={`fb-${runId}`} rows={2} maxLength={2000} value={comment} onChange={(e) => setComment(e.target.value)}
            placeholder={rating === -1 ? t('What was wrong, and what did you expect?') : t('What was good about it?')} />
          <div className="row">
            <button type="submit" className="btn small" disabled={busy}>{t('Save')}</button>
            <button type="button" className="btn ghost small" onClick={() => { setComment(saved); setOpen(false) }}>{t('Cancel')}</button>
            <span className="muted small">{t('Reviewers see your question, the answer and this comment.')}</span>
          </div>
        </form>
      )}
      {error && <span role="alert" className="error small">{error}</span>}
    </span>
  )
}
