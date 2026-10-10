import { useEffect, useState } from 'react'
import type { Api, Conflict, Step } from '../api'
import { fmt, t } from '../i18n'

/** Conflict ids announced by search_knowledge results ("Conflict: <id> options=k1,k3"). */
export function conflictIds(steps: Step[]): string[] {
  const ids = new Set<string>()
  for (const s of steps)
    if (s.kind === 'ToolCall' && s.name === 'search_knowledge' && s.result)
      for (const m of s.result.matchAll(/^Conflict: ([0-9a-f-]{36})/gm)) ids.add(m[1])
  return [...ids]
}

/** "The sources disagree": each alternative with its source and date, and a vote. Votes are a signal for owners, never authority. */
export function ConflictVote({ api, id }: { api: Api; id: string }) {
  const [conflict, setConflict] = useState<Conflict | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    api.conflict(id).then(setConflict).catch(() => setConflict(null))
  }, [api, id])
  if (!conflict) return null

  const vote = (option: string) =>
    api
      .voteConflict(id, option)
      .then((c) => {
        setConflict(c)
        setError(null)
      })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
  const votes = (option: string) => conflict.tally.options.find((o) => o.option === option)?.votes ?? 0

  return (
    <div className="conflict" role="group" aria-label={t('Sources disagree')}>
      <div className="label">{t('The sources disagree')}{conflict.summary ? `: ${conflict.summary}` : ''}</div>
      {conflict.status === 'resolved' && <p className="small">{t('An owner marked one option as authoritative ({who}).', { who: conflict.resolvedBy ?? '' })}</p>}
      <ul>
        {conflict.options.map((o) => (
          <li key={o.chunkId} className={conflict.resolvedOption === o.chunkId ? 'authoritative' : undefined}>
            <details>
              <summary>
                <strong>{o.title}</strong>
                <span className="muted small">
                  {' '}
                  · {o.sourceName} · updated {fmt.date(o.updatedAt)}
                  {o.changed ? ' · changed since detected' : ''}
                </span>
              </summary>
              <pre>{o.text}</pre>
            </details>
            <button type="button" className={`btn small${conflict.myVote === o.chunkId ? ' primary' : ''}`} onClick={() => void vote(o.chunkId)}>
              This is right ({votes(o.chunkId)})
            </button>
          </li>
        ))}
      </ul>
      <button type="button" className={`btn small${conflict.myVote === 'neither' ? ' primary' : ''}`} onClick={() => void vote('neither')}>
        Neither / not sure ({votes('neither')})
      </button>
      {error && <p className="error small">{error}</p>}
    </div>
  )
}

/** Admin view: open conflicts, tallies and the suggestion to pass to the source owner. */
export function ConflictList({ api }: { api: Api }) {
  const [list, setList] = useState<Conflict[] | null>(null)
  const load = () => {
    api.conflicts().then(setList).catch(() => setList(null)) // 403 for non-admins: the section stays hidden
  }
  useEffect(load, [api])
  if (!list || list.length === 0) return null
  return (
    <>
      <h2 className="section-title">{t('Conflicting sources')}</h2>
      <table>
        <thead>
          <tr>
            <th>{t('Question')}</th>
            <th>{t('What differs')}</th>
            <th>{t('Votes')}</th>
            <th>{t('Suggestion')}</th>
            <th>{t('Status')}</th>
          </tr>
        </thead>
        <tbody>
          {list.map((c) => {
            const suggested = c.options.find((o) => o.chunkId === c.tally.suggestion)
            return (
              <tr key={c.id}>
                <td>{c.question}</td>
                <td className="muted">{c.summary}</td>
                <td title={c.tally.options.map((o) => `${c.options.find((x) => x.chunkId === o.option)?.title ?? o.option}: ${o.weight}`).join('\n')}>
                  {c.tally.votes}
                  {c.tally.staleVotes ? <span className="muted small"> (+{c.tally.staleVotes} stale)</span> : null}
                  {c.tally.swing && <span className="warn small"> {t('sudden swing')}</span>}
                </td>
                <td>{suggested ? `${suggested.title} (${suggested.sourceName})` : <span className="muted">{t('no clear majority')}</span>}</td>
                <td>
                  {c.status === 'resolved' ? (
                    <span className="muted">{t('resolved by {who}', { who: c.resolvedBy ?? '' })}</span>
                  ) : (
                    c.options.map((o) => (
                      <button key={o.chunkId} type="button" className="btn small" onClick={() => void api.resolveConflict(c.id, o.chunkId).then(load)}>
                        Mark “{o.title}” authoritative
                      </button>
                    ))
                  )}
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </>
  )
}
