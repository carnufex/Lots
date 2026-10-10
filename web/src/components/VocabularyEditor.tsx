import { useEffect, useState } from 'react'
import { ApiError, type Api, type Vocabulary } from '../api'

/**
 * The user's dictation vocabulary: names, products and jargon that speech recognition should spell correctly.
 * Saved per user on the server; the words are sent to the speech service together with the recording.
 */
export default function VocabularyEditor({ api }: { api: Api }) {
  const [vocab, setVocab] = useState<Vocabulary | null>(null)
  const [draft, setDraft] = useState('')
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    api
      .getVocabulary()
      .then((v) => !cancelled && setVocab(v))
      .catch(() => !cancelled && setError('Could not load your vocabulary.'))
    return () => {
      cancelled = true
    }
  }, [api])

  const save = async (words: string[]) => {
    if (!vocab) return
    const previous = vocab
    setVocab({ ...vocab, words }) // optimistic
    setError(null)
    try {
      setVocab(await api.putVocabulary(words))
    } catch (e) {
      setVocab(previous)
      setError(e instanceof ApiError && e.status === 400 ? 'Too many words, or a word is too long (max 100 words, 60 characters each).' : 'Could not save your vocabulary.')
    }
  }

  const add = () => {
    if (!vocab) return
    const entered = draft.split(/[,\n;]/).map((w) => w.trim()).filter(Boolean)
    if (entered.length === 0) return
    setDraft('')
    // Same rule as the server (case-insensitive, no duplicates), so the list never shows a word twice while saving.
    const merged = [...vocab.words]
    for (const w of entered) if (!merged.some((x) => x.toLowerCase() === w.toLowerCase())) merged.push(w)
    if (merged.length !== vocab.words.length) void save(merged)
  }

  return (
    <details className="vocab">
      <summary>
        Vocabulary{vocab ? ` (${vocab.words.length})` : ''}
        <span className="muted"> · names and words dictation should spell correctly</span>
      </summary>
      <p className="muted small">
        Add names, products and jargon you say often (for example your name or a service). They are sent to the speech service with each
        dictation as a spelling hint. Only you can see and edit your own list.
      </p>
      {vocab && (
        <ul className="chips" aria-label="Vocabulary">
          {vocab.words.map((w) => (
            <li key={w} className="chip">
              {w}
              <button type="button" className="chipx" aria-label={`Remove ${w}`} onClick={() => void save(vocab.words.filter((x) => x !== w))}>
                ×
              </button>
            </li>
          ))}
          {vocab.shared
            .filter((w) => !vocab.words.some((x) => x.toLowerCase() === w.toLowerCase()))
            .map((w) => (
              <li key={`shared-${w}`} className="chip shared" title="Shared by everyone on this installation">
                {w}
              </li>
            ))}
          {vocab.words.length === 0 && vocab.shared.length === 0 && <li className="muted small">No words yet.</li>}
        </ul>
      )}
      <div className="row">
        <input
          className="wide"
          aria-label="Add words"
          placeholder="Add a word or several separated by commas"
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault()
              add()
            }
          }}
        />
        <button type="button" className="btn" disabled={!draft.trim() || !vocab} onClick={add}>
          Add
        </button>
      </div>
      {error && (
        <p role="alert" className="error small">
          {error}
        </p>
      )}
    </details>
  )
}
