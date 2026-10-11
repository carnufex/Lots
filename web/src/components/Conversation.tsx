import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, isTerminal, type Api, type VoiceLanguage } from '../api'
import type { ProfileInfo } from '../config'
import { MicSession } from '../voice/mic'
import { Player } from '../voice/player'
import { initialLanguage, saveLanguage } from '../voice/language'
import Avatar, { type AvatarState } from './Avatar'
import { t } from '../i18n'
import { defaultContext } from '../preferences'

type Message = { who: 'you' | 'agent'; text: string; runId?: string; failed?: boolean }

const STATUS: Record<AvatarState, string> = {
  off: t('Not in a conversation'),
  listening: t('Listening…'),
  hearing: t('Hearing you…'),
  thinking: t('Thinking…'),
  speaking: t('Speaking: talk to interrupt'),
}

const SENSITIVITY: Record<string, number> = { low: 0.7, normal: 1, high: 1.5 }
const ACK_AFTER_MS = 900
const POLL_MS = 350
const GIVE_UP_MS = 60_000 // a turn that has not answered by now is stuck: say so instead of staying silent

const newId = () => crypto.randomUUID()

/**
 * Hands-free conversation: start, talk, talk, the agent answers aloud, talk over it and it stops. Each spoken turn is an
 * ordinary run (so it is audited and visible in the run list) that remembers the earlier turns of the conversation.
 * Voice is only a channel: approvals and everything else still happen with explicit clicks (ADR 0013).
 */
export default function Conversation({
  api,
  profiles,
  defaultLanguage,
  conversationId: joinId,
  onTurn,
  onEnd,
  showTranscript = true,
  context,
}: {
  api: Api
  profiles: ProfileInfo[]
  defaultLanguage: VoiceLanguage
  /** Chat (#152): speak inside this conversation instead of starting a new one. */
  conversationId?: string
  /** A spoken turn has its answer (the chat reloads its thread). */
  onTurn?: (conversationId: string) => void
  /** The conversation was ended; turns tells whether anything was said. */
  onEnd?: (conversationId: string, turns: number) => void
  /** Chat shows the turns in its own thread, so the transcript here is optional. */
  showTranscript?: boolean
  /** The chat's context choice; '' lets the shell choose (#150). Without it the conversation offers its own picker. */
  context?: string
}) {
  const [state, setState] = useState<AvatarState>('off')
  const [messages, setMessages] = useState<Message[]>([])
  const [error, setError] = useState<string | null>(null)
  const [language, setLanguageState] = useState<VoiceLanguage>(() => initialLanguage(defaultLanguage))
  const [profile, setProfile] = useState(() => defaultContext(profiles))
  const [sensitivity, setSensitivity] = useState('normal')
  const [muted, setMuted] = useState(false)

  const stateRef = useRef<AvatarState>('off')
  const mic = useRef<MicSession | null>(null)
  const player = useRef(new Player())
  const turn = useRef(0) // a newer turn (or microphone off) cancels the one in flight
  const conversationId = useRef<string>(newId())
  const inputLevel = useRef(0)
  const level = useRef(0)
  const languageRef = useRef(language)
  const profileRef = useRef(profile)
  useEffect(() => {
    languageRef.current = language
    profileRef.current = context ?? profile
  }, [language, profile, context])
  const log = useRef<HTMLOListElement>(null)
  const turns = useRef(0)
  const hooks = useRef({ onTurn, onEnd })
  useEffect(() => {
    hooks.current = { onTurn, onEnd }
  }, [onTurn, onEnd])

  const go = useCallback((s: AvatarState) => {
    stateRef.current = s
    setState(s)
    mic.current?.setPlaybackActive(s === 'speaking')
  }, [])

  const add = (m: Message) => setMessages((all) => [...all, m])

  // The avatar follows the microphone while you speak and the agent's voice while it speaks.
  useEffect(() => {
    let raf = 0
    const tick = () => {
      level.current = stateRef.current === 'speaking' ? player.current.level() : inputLevel.current
      raf = requestAnimationFrame(tick)
    }
    raf = requestAnimationFrame(tick)
    return () => cancelAnimationFrame(raf)
  }, [])

  useEffect(() => {
    log.current?.scrollTo({ top: log.current.scrollHeight })
  }, [messages])

  const speakNow = useCallback(
    async (blob: Blob, mine: number) => {
      if (mine !== turn.current) return
      go('speaking')
      await player.current.play(blob)
      if (mine === turn.current && stateRef.current === 'speaking') go('listening')
    },
    [go],
  )

  const handleUtterance = useCallback(
    async (wav: Blob) => {
      const mine = ++turn.current
      player.current.stop()
      go('thinking')
      try {
        const heard = await api.transcribe(wav, languageRef.current, conversationId.current)
        if (mine !== turn.current) return
        const text = heard.text.trim()
        if (!text) {
          go('listening')
          return
        }
        add({ who: 'you', text })
        const spoken: VoiceLanguage = heard.language === 'en' ? 'en' : languageRef.current === 'en' ? 'en' : 'sv'

        const started = await api.startRun(text, profileRef.current || null, { voice: true, conversationId: conversationId.current })
        if (mine !== turn.current) return
        if ('choose' in started) {
          // Never a silent guess (#150): say so, and let the user pick the context in the chat.
          add({ who: 'agent', text: t('I am not sure where this belongs: {list}. Pick a context in the chat and ask again.', { list: started.choose.candidates.map((c) => c.profile).join(', ') }), failed: true })
          go('listening')
          return
        }
        const run = started

        // A short fixed acknowledgement if the answer takes a moment, so it never feels like silence.
        const ack = window.setTimeout(() => {
          void api.ack(spoken).then((blob) => {
            if (blob && mine === turn.current && stateRef.current === 'thinking') void player.current.play(blob)
          })
        }, ACK_AFTER_MS)

        // Speak the answer sentence by sentence while it is written (#37): the first sentence plays while the rest is generated.
        const speaker = (async () => {
          let from = 0
          let hash: string | undefined
          let spoke = false
          let pending: ReturnType<typeof api.speakNext> | null = api.speakNext(run.id, from, hash)
          while (pending && mine === turn.current) {
            const r: Awaited<ReturnType<typeof api.speakNext>> = await pending
            if (r.reset) { from = 0; hash = undefined }
            if (r.audio) {
              from = r.to
              hash = r.hash
              pending = r.done ? null : api.speakNext(run.id, from, hash) // fetch the next sentence while this one plays
              window.clearTimeout(ack)
              if (!spoke) player.current.stop() // the acknowledgement, if it is still playing
              spoke = true
              if (mine !== turn.current) break
              go('speaking')
              await player.current.play(r.audio)
              continue
            }
            if (r.done) break
            await new Promise((ok) => setTimeout(ok, 250))
            pending = mine === turn.current ? api.speakNext(run.id, from, hash) : null
          }
          return spoke
        })().catch(() => false)

        const deadline = Date.now() + GIVE_UP_MS
        let detail = await api.getRun(run.id)
        while (!isTerminal(detail.status) && mine === turn.current && Date.now() < deadline) {
          await new Promise((r) => setTimeout(r, POLL_MS))
          detail = await api.getRun(run.id)
        }
        window.clearTimeout(ack)
        if (mine !== turn.current) return
        if (!isTerminal(detail.status)) {
          void api.cancelRun(run.id).catch(() => undefined) // nobody is waiting for it any more
          player.current.stop()
          add({ who: 'agent', text: t('This is taking too long, so I stopped waiting. Try again in a moment.'), runId: run.id, failed: true })
          go('listening')
          return
        }

        if (detail.status === 'Failed' || !detail.finalAnswer) {
          add({ who: 'agent', text: detail.error ?? t('Something went wrong.'), runId: run.id, failed: true })
          go('listening')
          return
        }
        add({ who: 'agent', text: detail.finalAnswer, runId: run.id })
        turns.current++
        hooks.current.onTurn?.(conversationId.current)
        if (await speaker) {
          if (mine === turn.current && stateRef.current === 'speaking') go('listening')
          return
        }
        // Sentence streaming unavailable (an older shell): the whole answer at once.
        const audio = await api.speak(run.id, 'auto')
        player.current.stop() // the acknowledgement, if it is still playing
        await speakNow(audio, mine)
      } catch (e) {
        if (mine !== turn.current) return
        setError(
          e instanceof ApiError && e.status === 503
            ? t('Voice is unavailable right now. You can still type your question.')
            : t('Something went wrong with that turn. Try again.'),
        )
        go('listening')
      }
    },
    [api, go, speakNow],
  )

  const start = async () => {
    setError(null)
    setMuted(false)
    // Every call is its own conversation (and its own history entry), unless the chat asks to continue one.
    conversationId.current = joinId ?? newId()
    turns.current = 0
    setMessages([])
    player.current.prepare() // inside the click: lets the browser play audio later without another gesture
    try {
      mic.current = await MicSession.start(
        {
          onLevel: (l) => {
            inputLevel.current = l
          },
          onSpeechStart: (during) => {
            // Talking over the agent: stop it at once and drop whatever it was about to say.
            if (during || stateRef.current === 'thinking' || stateRef.current === 'speaking') {
              turn.current++
              player.current.stop()
            }
            go('hearing')
          },
          onUtterance: (wav) => void handleUtterance(wav),
          onAbort: () => go('listening'),
        },
        SENSITIVITY[sensitivity],
      )
      go('listening')
    } catch {
      setError(t('Microphone access was blocked. Allow it in the browser to talk to the agent.'))
    }
  }

  /** Ends the conversation: stops listening and talking. The transcript stays on screen until you start the next one. */
  const end = useCallback(() => {
    turn.current++
    mic.current?.stop()
    mic.current = null
    player.current.stop()
    inputLevel.current = 0
    setMuted(false)
    go('off')
    hooks.current.onEnd?.(conversationId.current, turns.current)
  }, [go])

  /** Mute only silences you: the conversation, and any answer the agent is giving or preparing, carry on. */
  const toggleMute = () => {
    const next = !muted
    setMuted(next)
    mic.current?.setMuted(next)
    if (next) {
      inputLevel.current = 0
      if (stateRef.current === 'hearing') go('listening') // what you were saying is dropped
    }
  }

  useEffect(() => {
    mic.current?.setSensitivity(SENSITIVITY[sensitivity])
  }, [sensitivity])

  useEffect(
    () => () => {
      turn.current++
      mic.current?.stop()
      player.current.dispose()
    },
    [],
  )

  const on = state !== 'off'
  const idleMuted = muted && (state === 'listening' || state === 'hearing')
  const statusText = idleMuted ? t('Muted: the agent cannot hear you') : STATUS[state]

  return (
    <section className={`talk ${state} ${muted ? 'ismuted' : ''}`} aria-label={t('Conversation')}>
      <div className="talkhead">
        <Avatar state={idleMuted ? 'off' : state} level={level} />
        <div className="talkside">
          <div className="talkstate" role="status" aria-live="polite" data-testid="conversation-state" data-state={state}>
            {statusText}
          </div>
          {on ? (
            <div className="row callbuttons">
              <button type="button" className={`btn mutetoggle ${muted ? 'muted' : ''}`} aria-pressed={muted} onClick={toggleMute}>
                {muted ? 'Unmute' : 'Mute'}
              </button>
              <button type="button" className="btn endcall" onClick={end}>
                {t('End conversation')}
              </button>
            </div>
          ) : (
            <>
              <button type="button" className="btn startcall" onClick={() => void start()}>
                {t('Start conversation')}
              </button>
              <span className="muted small">{t('Both sides are recorded and kept 30 days (History lets you play or delete them).')}</span>
            </>
          )}
          <div className="row talkopts">
            <select
              aria-label={t('Conversation language')}
              value={language}
              onChange={(e) => {
                const l = e.target.value as VoiceLanguage
                setLanguageState(l)
                saveLanguage(l)
              }}
            >
              <option value="sv">Svenska</option>
              <option value="en">English</option>
              <option value="auto">{t('Auto')}</option>
            </select>
            <select aria-label={t('Microphone sensitivity')} value={sensitivity} onChange={(e) => setSensitivity(e.target.value)}>
              <option value="low">{t('Sensitivity: low')}</option>
              <option value="normal">{t('Sensitivity: normal')}</option>
              <option value="high">{t('Sensitivity: high')}</option>
            </select>
            {context === undefined && profiles.length > 1 && (
              <select aria-label={t('Context')} value={profile} onChange={(e) => setProfile(e.target.value)}>
                {profiles.map((p) => (
                  <option key={p.name} value={p.name}>
                    {p.name}
                  </option>
                ))}
              </select>
            )}
          </div>
          <p className="muted small">{t('Headphones work best: the agent then never hears itself.')}</p>
          {error && (
            <p role="alert" className="error small">
              {error}
            </p>
          )}
        </div>
      </div>
      {showTranscript && messages.length > 0 && (
        <ol className="transcript" ref={log} aria-label={t('Conversation so far')}>
          {messages.map((m, i) => (
            <li key={i} className={`line ${m.who} ${m.failed ? 'failed' : ''}`}>
              <span className="who">{m.who === 'you' ? 'You' : 'Agent'}</span>
              <span className="said">{m.text}</span>
              {m.runId && (
                <a className="small" href={`#/runs/${m.runId}`}>
                  details
                </a>
              )}
            </li>
          ))}
        </ol>
      )}
    </section>
  )
}
