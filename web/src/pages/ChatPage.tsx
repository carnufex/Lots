import { useCallback, useEffect, useRef, useState } from 'react'
import Feedback from '../components/Feedback'
import { type Api, type ConversationDetail, type ConversationSummary, type ConversationTurn, type RouteChoice } from '../api'
import type { ProfileInfo, VoiceConfig } from '../config'
import Conversation from '../components/Conversation'
import MicButton from '../components/MicButton'
import { initialLanguage, saveLanguage } from '../voice/language'
import type { VoiceLanguage } from '../api'
import Markdown from '../components/Markdown'
import { AttachmentList, AttachPicker, type UploadedAttachment } from '../components/Attachments'
import { fmt, t } from '../i18n'
import { preferredContext } from '../preferences'

const ACTIVE = ['Pending', 'Running', 'WaitingForApproval']

/**
 * Chat (#95): conversations as threads, answers streamed as they are written, stop, regenerate, edit the last question, copy and
 * share. Text and voice turns share one history (the same conversation ids as conversation mode).
 */
export default function ChatPage({ api, profiles, id, voice }: { api: Api; profiles: ProfileInfo[]; id?: string; voice?: VoiceConfig }) {
  // Voice is a mode of a conversation (#152): hands-free talking and dictation both happen here, in the same thread.
  const [talking, setTalking] = useState(false)
  const [speech, setSpeech] = useState<VoiceLanguage>(() => initialLanguage(voice?.defaultLanguage ?? 'auto'))
  const [list, setList] = useState<ConversationSummary[]>([])
  const [detail, setDetail] = useState<ConversationDetail | null>(null)
  const [live, setLive] = useState<{ runId: string; partial: string } | null>(null)
  const [prompt, setPrompt] = useState('')
  const [files, setFiles] = useState<UploadedAttachment[]>([])
  // '' = Automatic (#150): the shell picks the context per message; a choice here overrides it.
  const [profile, setProfile] = useState(() => preferredContext(profiles))
  const [ask, setAsk] = useState<{ text: string; choice: RouteChoice } | null>(null)
  const [editing, setEditing] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [note, setNote] = useState<string | null>(null)
  const following = useRef<AbortController | null>(null)
  const endRef = useRef<HTMLDivElement | null>(null)

  const loadList = useCallback(() => {
    void api.listConversations().then((l) => setList(l.conversations), () => setList([]))
  }, [api])

  const loadDetail = useCallback(async (): Promise<ConversationDetail | null> => {
    if (!id) return null // a new chat: nothing to load (the page is keyed by conversation, so no stale detail)
    try {
      const d = await api.getConversation(id)
      setDetail(d)
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
      const last = d?.turns.filter((x) => !x.superseded).at(-1)
      if (last && ACTIVE.includes(last.status)) void follow(last.runId)
    })
    return () => following.current?.abort()
  }, [loadDetail, follow])
  useEffect(() => {
    // A block, not an expression: newer browsers return a Promise from scrollIntoView, which React would take for a cleanup.
    endRef.current?.scrollIntoView({ block: 'end' })
  }, [detail, live?.partial])

  const turns = detail?.turns.filter((x) => !x.superseded) ?? []
  const endTalk = (conversationId: string, spoken: number) => {
    setTalking(false)
    if (!id && spoken > 0) window.location.hash = `#/chat/${conversationId}` // the new conversation's thread
  }
  const last = turns.at(-1)
  const busy = live !== null

  const send = async (picked?: string | string[]) => {
    const text = (picked ? ask?.text : prompt.trim()) ?? ''
    if (!text || busy) return
    setError(null)
    setAsk(null)
    const conversationId = id ?? crypto.randomUUID()
    const several = Array.isArray(picked) ? picked : undefined
    const one = typeof picked === 'string' ? picked : undefined
    try {
      const started = await api.startRun(text, one ?? (several ? null : profile || null), {
        conversationId,
        attachments: files.map((f) => f.id),
        ...(picked ? { routing: 'chosen' as const } : {}),
        ...(several ? { contexts: several } : {}),
      })
      if ('choose' in started) {
        setAsk({ text, choice: started.choose }) // one click, never a silent guess
        return
      }
      const run = started
      setPrompt('')
      setFiles([])
      if (!id) window.location.hash = `#/chat/${conversationId}`
      void follow(run.id)
    } catch (e) {
      setError(String(e))
    }
  }

  const regenerate = async (turn: ConversationTurn, edited?: string, context?: string) => {
    setError(null)
    try {
      const run = await api.regenerateRun(turn.runId, edited, context)
      setEditing(null)
      void follow(run.id)
    } catch (e) {
      setError(String(e))
    }
  }

  const copy = async (text: string, what: string) => {
    await navigator.clipboard.writeText(text)
    setNote(t('{what} copied', { what }))
    window.setTimeout(() => setNote(null), 2000)
  }

  return (
    <section className="chat">
      <aside className="chat-list" aria-label={t('Conversations')}>
        <a className="btn primary small" href="#/chat">
          {t('New chat')}
        </a>
        <ul>
          {list.map((c) => (
            <li key={c.id} className={c.id === id ? 'active' : undefined}>
              <a href={`#/chat/${c.id}`} title={c.summary ?? c.title}>
                {c.voice && <span className="muted small">🎙 </span>}
                {c.title}
              </a>
              <span className="muted small">{fmt.date(c.endedAt)}</span>
            </li>
          ))}
        </ul>
      </aside>

      <div className="chat-main">
        {talking && voice?.enabled && (
          <Conversation
            api={api}
            profiles={profiles}
            context={profile}
            defaultLanguage={voice.defaultLanguage}
            conversationId={id}
            showTranscript={!id}
            onTurn={() => {
              void loadDetail()
              loadList()
            }}
            onEnd={endTalk}
          />
        )}
        <div className="chat-thread" aria-live="polite">
          {turns.length === 0 && !live && <p className="muted">{t("Ask anything your profile's tools can answer. Earlier turns are part of the context.")}</p>}
          {turns.map((turn) => {
            const isLast = turn === last
            const answer = live?.runId === turn.runId ? live.partial : turn.answer
            return (
              <article key={turn.runId} className="turn">
                <div className="turn-user">
                  {editing === turn.runId ? (
                    <EditBox initial={turn.prompt} onCancel={() => setEditing(null)} onSave={(p) => void regenerate(turn, p)} />
                  ) : (
                    <>
                      <p>
                        {turn.voice && <span className="muted small" title={t('Spoken')}>🎙 </span>}
                        {turn.prompt}
                      </p>
                      <AttachmentList api={api} items={turn.attachments ?? []} />
                      {isLast && !busy && (
                        <button type="button" className="btn ghost small" onClick={() => setEditing(turn.runId)}>
                          {t('Edit')}
                        </button>
                      )}
                    </>
                  )}
                </div>
                <div className="turn-agent">
                  {turn.profile && profiles.length > 1 && (
                    <p className="turn-context small muted">
                      {(turn.contexts ?? [turn.profile]).map((c) => (
                        <span key={c} className="chip" title={turn.routing ? t('Chosen: {how}', { how: t(turn.routing) }) : undefined}>
                          {c}
                        </span>
                      ))}
                      {isLast && !busy && !ACTIVE.includes(turn.status) &&
                        profiles
                          .filter((p) => p.name !== turn.profile)
                          .map((p) => (
                            <button key={p.name} type="button" className="btn ghost small" onClick={() => void regenerate(turn, undefined, p.name)}>
                              {t('Use {context} instead', { context: p.name })}
                            </button>
                          ))}
                    </p>
                  )}
                  {answer ? <Markdown text={answer} /> : ACTIVE.includes(turn.status) ? <p className="muted">{turn.status === 'WaitingForApproval' ? t('Waiting for an approval…') : t('Working…')}</p> : null}
                  {turn.status === 'WaitingForApproval' && (
                    <p className="small">
                      {t('A tool call needs an approval:')} <a href="#/approvals">{t('Approvals')}</a>
                    </p>
                  )}
                  {(turn.status === 'Failed' || turn.status === 'Cancelled') && <p className="error small">{turn.error ?? turn.status}</p>}
                  <div className="turn-actions">
                    {turn.answer && <Feedback api={api} runId={turn.runId} rating={turn.rating} comment={turn.feedbackComment} />}
                    {turn.answer && (
                      <button type="button" className="btn ghost small" onClick={() => void copy(turn.answer!, t('Answer'))}>
                        {t('Copy')}
                      </button>
                    )}
                    <button type="button" className="btn ghost small" onClick={() => void copy(`${window.location.origin}/#/runs/${turn.runId}`, t('Link'))}
                      title={t("A link to this turn's run. Only you and admins can open it.")}>
                      {t('Share')}
                    </button>
                    <a className="btn ghost small" href={`#/runs/${turn.runId}`}>
                      {t('Trace')}
                    </a>
                    {isLast && !busy && !ACTIVE.includes(turn.status) && (
                      <button type="button" className="btn ghost small" onClick={() => void regenerate(turn)}>
                        {t('Regenerate')}
                      </button>
                    )}
                  </div>
                </div>
              </article>
            )
          })}
          {live && !turns.some((turn) => turn.runId === live.runId) && (
            <article className="turn">
              <div className="turn-agent">{live.partial ? <Markdown text={live.partial} /> : <p className="muted">{t('Working…')}</p>}</div>
            </article>
          )}
          <div ref={endRef} />
        </div>

        {ask && (
          <div className="route-choice card" role="group" aria-label={t('Choose a context')}>
            <p className="small">{t('Where does this belong?')} <span className="muted">{ask.choice.reason}</span></p>
            <div className="row">
              {ask.choice.candidates.map((c) => (
                <button key={c.profile} type="button" className="btn" onClick={() => void send(c.profile)}>
                  {c.profile}
                  {!c.readOnly && <span className="muted small"> {t('(can change things)')}</span>}
                </button>
              ))}
              {ask.choice.candidates.filter((c) => c.readOnly).length >= 2 && (
                <button type="button" className="btn" title={t('Each context answers on its own (read only); the answers are combined.')}
                  onClick={() => void send(ask.choice.candidates.filter((c) => c.readOnly).slice(0, 3).map((c) => c.profile))}>
                  {t('Ask them all')}
                </button>
              )}
              <button type="button" className="btn ghost" onClick={() => setAsk(null)}>
                {t('Cancel')}
              </button>
            </div>
          </div>
        )}
        <form
          className="chat-input"
          onSubmit={(e) => {
            e.preventDefault()
            void send()
          }}
        >
          <label className="sr" htmlFor="chat-prompt">
            {t('Message')}
          </label>
          <textarea
            id="chat-prompt"
            rows={2}
            value={prompt}
            placeholder={busy ? t('The agent is answering…') : t('Message (Enter to send, Shift+Enter for a new line)')}
            onChange={(e) => setPrompt(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault()
                void send()
              }
            }}
          />
          <div className="row">
            {profiles.length > 1 ? (
              <select aria-label={t('Context')} value={profile} onChange={(e) => setProfile(e.target.value)}>
                <option value="">{t('Automatic')}</option>
                {profiles.map((p) => (
                  <option key={p.name} value={p.name}>
                    {p.name}
                  </option>
                ))}
              </select>
            ) : (
              <span className="muted small">{profile}</span>
            )}
            <AttachPicker api={api} files={files} onChange={setFiles} disabled={busy} />
            <span className="grow" />
            {voice?.enabled && (
              <>
                <select aria-label={t('Speech language')} value={speech} onChange={(e) => { const l = e.target.value as VoiceLanguage; setSpeech(l); saveLanguage(l) }}>
                  <option value="auto">{t('Auto')}</option>
                  <option value="sv">Svenska</option>
                  <option value="en">English</option>
                </select>
                <MicButton api={api} language={speech} onText={(text) => setPrompt((p) => (p.trim() ? `${p.trim()} ${text}` : text))} />
                <button type="button" className={talking ? 'btn active' : 'btn'} aria-pressed={talking} onClick={() => setTalking((v) => !v)}
                  title={t('Talk hands-free: the agent answers aloud and you can interrupt it')}>
                  {talking ? t('Hide voice') : t('Voice mode')}
                </button>
              </>
            )}
            {note && <span className="muted small">{note}</span>}
            {error && (
              <span role="alert" className="error small">
                {error}
              </span>
            )}
            {busy ? (
              <button type="button" className="btn" onClick={() => void api.cancelRun(live.runId)}>
                {t('Stop')}
              </button>
            ) : (
              <button type="submit" className="btn primary" disabled={!prompt.trim()}>
                {t('Send')}
              </button>
            )}
          </div>
          {voice?.enabled && (
            <p className="small muted">
              {t('Dictation misspells a name?')} <a href="#/voice/vocabulary">{t('Add it to your vocabulary')}</a>
            </p>
          )}
        </form>
      </div>
    </section>
  )
}

function EditBox({ initial, onSave, onCancel }: { initial: string; onSave: (p: string) => void; onCancel: () => void }) {
  const [value, setValue] = useState(initial)
  return (
    <div className="editbox">
      <textarea rows={2} value={value} onChange={(e) => setValue(e.target.value)} aria-label={t('Edit your question')} />
      <div className="row">
        <button type="button" className="btn primary small" disabled={!value.trim()} onClick={() => onSave(value.trim())}>
          {t('Save and ask again')}
        </button>
        <button type="button" className="btn small" onClick={onCancel}>
          {t('Cancel')}
        </button>
      </div>
    </div>
  )
}
