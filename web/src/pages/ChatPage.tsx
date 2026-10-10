import { useCallback, useEffect, useRef, useState } from 'react'
import { type Api, type ConversationDetail, type ConversationSummary, type ConversationTurn } from '../api'
import type { ProfileInfo } from '../config'
import Markdown from '../components/Markdown'

const ACTIVE = ['Pending', 'Running', 'WaitingForApproval']

/**
 * Chat (#95): conversations as threads, answers streamed as they are written, stop, regenerate, edit the last question, copy and
 * share. Text and voice turns share one history (the same conversation ids as conversation mode).
 */
export default function ChatPage({ api, profiles, id }: { api: Api; profiles: ProfileInfo[]; id?: string }) {
  const [list, setList] = useState<ConversationSummary[]>([])
  const [detail, setDetail] = useState<ConversationDetail | null>(null)
  const [live, setLive] = useState<{ runId: string; partial: string } | null>(null)
  const [prompt, setPrompt] = useState('')
  const [profile, setProfile] = useState(profiles[0]?.name ?? '')
  const [editing, setEditing] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [note, setNote] = useState<string | null>(null)
  const following = useRef<AbortController | null>(null)
  const endRef = useRef<HTMLDivElement | null>(null)

  const loadList = useCallback(() => {
    api.listConversations().then((l) => setList(l.conversations)).catch(() => setList([]))
  }, [api])

  const loadDetail = useCallback(async (): Promise<ConversationDetail | null> => {
    if (!id) {
      setDetail(null)
      return null
    }
    try {
      const d = await api.getConversation(id)
      setDetail(d)
      if (d.conversation.profile) setProfile(d.conversation.profile)
      return d
    } catch {
      setDetail(null) // a new conversation has no runs yet
      return null
    }
  }, [api, id])

  const follow = useCallback(
    async (runId: string) => {
      following.current?.abort()
      const ctrl = new AbortController()
      following.current = ctrl
      setLive({ runId, partial: '' })
      try {
        await api.streamRun(runId, { onPartial: (text) => setLive({ runId, partial: text }), onChanged: () => void loadDetail() }, ctrl.signal)
      } catch {
        // the stream ended or was aborted: the conversation below is reloaded either way
      }
      if (following.current === ctrl) {
        setLive(null)
        following.current = null
      }
      await loadDetail()
      loadList()
    },
    [api, loadDetail, loadList],
  )

  useEffect(loadList, [loadList])
  useEffect(() => {
    void loadDetail().then((d) => {
      const last = d?.turns.filter((t) => !t.superseded).at(-1)
      if (last && ACTIVE.includes(last.status)) void follow(last.runId)
    })
    return () => following.current?.abort()
  }, [loadDetail, follow])
  useEffect(() => {
    // A block, not an expression: newer browsers return a Promise from scrollIntoView, which React would take for a cleanup.
    endRef.current?.scrollIntoView({ block: 'end' })
  }, [detail, live?.partial])

  const turns = detail?.turns.filter((t) => !t.superseded) ?? []
  const last = turns.at(-1)
  const busy = live !== null

  const send = async () => {
    const text = prompt.trim()
    if (!text || busy) return
    setError(null)
    const conversationId = id ?? crypto.randomUUID()
    try {
      const run = await api.startRun(text, profile, { conversationId })
      setPrompt('')
      if (!id) window.location.hash = `#/chat/${conversationId}`
      void follow(run.id)
    } catch (e) {
      setError(String(e))
    }
  }

  const regenerate = async (turn: ConversationTurn, edited?: string) => {
    setError(null)
    try {
      const run = await api.regenerateRun(turn.runId, edited)
      setEditing(null)
      void follow(run.id)
    } catch (e) {
      setError(String(e))
    }
  }

  const copy = async (text: string, what: string) => {
    await navigator.clipboard.writeText(text)
    setNote(`${what} copied`)
    window.setTimeout(() => setNote(null), 2000)
  }

  return (
    <section className="chat">
      <aside className="chat-list" aria-label="Conversations">
        <a className="btn primary small" href="#/chat">
          New chat
        </a>
        <ul>
          {list.map((c) => (
            <li key={c.id} className={c.id === id ? 'active' : undefined}>
              <a href={`#/chat/${c.id}`} title={c.summary ?? c.title}>
                {c.voice && <span className="muted small">🎙 </span>}
                {c.title}
              </a>
              <span className="muted small">{new Date(c.endedAt).toLocaleDateString()}</span>
            </li>
          ))}
        </ul>
      </aside>

      <div className="chat-main">
        <div className="chat-thread" aria-live="polite">
          {turns.length === 0 && !live && <p className="muted">Ask anything your profile's tools can answer. Earlier turns are part of the context.</p>}
          {turns.map((t) => {
            const isLast = t === last
            const answer = live?.runId === t.runId ? live.partial : t.answer
            return (
              <article key={t.runId} className="turn">
                <div className="turn-user">
                  {editing === t.runId ? (
                    <EditBox initial={t.prompt} onCancel={() => setEditing(null)} onSave={(p) => void regenerate(t, p)} />
                  ) : (
                    <>
                      <p>
                        {t.voice && <span className="muted small" title="Spoken">🎙 </span>}
                        {t.prompt}
                      </p>
                      {isLast && !busy && (
                        <button type="button" className="btn ghost small" onClick={() => setEditing(t.runId)}>
                          Edit
                        </button>
                      )}
                    </>
                  )}
                </div>
                <div className="turn-agent">
                  {answer ? <Markdown text={answer} /> : ACTIVE.includes(t.status) ? <p className="muted">{t.status === 'WaitingForApproval' ? 'Waiting for an approval…' : 'Working…'}</p> : null}
                  {t.status === 'WaitingForApproval' && (
                    <p className="small">
                      A tool call needs an approval: <a href="#/approvals">Approvals</a>
                    </p>
                  )}
                  {(t.status === 'Failed' || t.status === 'Cancelled') && <p className="error small">{t.error ?? t.status}</p>}
                  <div className="turn-actions">
                    {t.answer && (
                      <button type="button" className="btn ghost small" onClick={() => void copy(t.answer!, 'Answer')}>
                        Copy
                      </button>
                    )}
                    <button type="button" className="btn ghost small" onClick={() => void copy(`${window.location.origin}/#/runs/${t.runId}`, 'Link')}
                      title="A link to this turn's run. Only you and admins can open it.">
                      Share
                    </button>
                    <a className="btn ghost small" href={`#/runs/${t.runId}`}>
                      Trace
                    </a>
                    {isLast && !busy && !ACTIVE.includes(t.status) && (
                      <button type="button" className="btn ghost small" onClick={() => void regenerate(t)}>
                        Regenerate
                      </button>
                    )}
                  </div>
                </div>
              </article>
            )
          })}
          {live && !turns.some((t) => t.runId === live.runId) && (
            <article className="turn">
              <div className="turn-agent">{live.partial ? <Markdown text={live.partial} /> : <p className="muted">Working…</p>}</div>
            </article>
          )}
          <div ref={endRef} />
        </div>

        <form
          className="chat-input"
          onSubmit={(e) => {
            e.preventDefault()
            void send()
          }}
        >
          <label className="sr" htmlFor="chat-prompt">
            Message
          </label>
          <textarea
            id="chat-prompt"
            rows={2}
            value={prompt}
            placeholder={busy ? 'The agent is answering…' : 'Message (Enter to send, Shift+Enter for a new line)'}
            onChange={(e) => setPrompt(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault()
                void send()
              }
            }}
          />
          <div className="row">
            {profiles.length > 1 && !id ? (
              <select aria-label="Profile" value={profile} onChange={(e) => setProfile(e.target.value)}>
                {profiles.map((p) => (
                  <option key={p.name} value={p.name}>
                    {p.name}
                  </option>
                ))}
              </select>
            ) : (
              <span className="muted small">{profile}</span>
            )}
            <span className="grow" />
            {note && <span className="muted small">{note}</span>}
            {error && (
              <span role="alert" className="error small">
                {error}
              </span>
            )}
            {busy ? (
              <button type="button" className="btn" onClick={() => void api.cancelRun(live.runId)}>
                Stop
              </button>
            ) : (
              <button type="submit" className="btn primary" disabled={!prompt.trim()}>
                Send
              </button>
            )}
          </div>
        </form>
      </div>
    </section>
  )
}

function EditBox({ initial, onSave, onCancel }: { initial: string; onSave: (p: string) => void; onCancel: () => void }) {
  const [value, setValue] = useState(initial)
  return (
    <div className="editbox">
      <textarea rows={2} value={value} onChange={(e) => setValue(e.target.value)} aria-label="Edit your question" />
      <div className="row">
        <button type="button" className="btn primary small" disabled={!value.trim()} onClick={() => onSave(value.trim())}>
          Save and ask again
        </button>
        <button type="button" className="btn small" onClick={onCancel}>
          Cancel
        </button>
      </div>
    </div>
  )
}
