import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, isTerminal, type Api, type VoiceLanguage } from '../api'
import type { ProfileInfo } from '../config'
import { MicSession } from '../voice/mic'
import { Player } from '../voice/player'
import { initialLanguage, saveLanguage } from '../voice/language'
import Avatar, { type AvatarState } from './Avatar'

type Message = { who: 'you' | 'agent'; text: string; runId?: string; failed?: boolean }

const STATUS: Record<AvatarState, string> = {
  off: 'Microphone off',
  listening: 'Listening…',
  hearing: 'Hearing you…',
  thinking: 'Thinking…',
  speaking: 'Speaking: talk to interrupt',
}

const SENSITIVITY: Record<string, number> = { low: 0.7, normal: 1, high: 1.5 }
const ACK_AFTER_MS = 900
const POLL_MS = 350

const newId = () => crypto.randomUUID()

/**
 * Hands-free conversation: microphone on, talk, the agent answers aloud, talk over it and it stops. Each spoken turn is an
 * ordinary run (so it is audited and visible in the run list) that remembers the earlier turns of the conversation.
 * Voice is only a channel: approvals and everything else still happen with explicit clicks (ADR 0013).
 */
export default function Conversation({
  api,
  profiles,
  defaultLanguage,
}: {
  api: Api
  profiles: ProfileInfo[]
  defaultLanguage: VoiceLanguage
}) {
  const [state, setState] = useState<AvatarState>('off')
  const [messages, setMessages] = useState<Message[]>([])
  const [error, setError] = useState<string | null>(null)
  const [language, setLanguageState] = useState<VoiceLanguage>(() => initialLanguage(defaultLanguage))
  const [profile, setProfile] = useState(profiles[0]?.name ?? '')
  const [sensitivity, setSensitivity] = useState('normal')

  const stateRef = useRef<AvatarState>('off')
  const mic = useRef<MicSession | null>(null)
  const player = useRef(new Player())
  const turn = useRef(0) // a newer turn (or microphone off) cancels the one in flight
  const conversationId = useRef(newId())
  const inputLevel = useRef(0)
  const level = useRef(0)
  const languageRef = useRef(language)
  const profileRef = useRef(profile)
  useEffect(() => {
    languageRef.current = language
    profileRef.current = profile
  }, [language, profile])
  const log = useRef<HTMLOListElement>(null)

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
        const heard = await api.transcribe(wav, languageRef.current)
        if (mine !== turn.current) return
        const text = heard.text.trim()
        if (!text) {
          go('listening')
          return
        }
        add({ who: 'you', text })
        const spoken: VoiceLanguage = heard.language === 'en' ? 'en' : languageRef.current === 'en' ? 'en' : 'sv'

        const run = await api.startRun(text, profileRef.current, { voice: true, conversationId: conversationId.current })
        if (mine !== turn.current) return

        // A short fixed acknowledgement if the answer takes a moment, so it never feels like silence.
        const ack = window.setTimeout(() => {
          void api.ack(spoken).then((blob) => {
            if (blob && mine === turn.current && stateRef.current === 'thinking') void player.current.play(blob)
          })
        }, ACK_AFTER_MS)

        let detail = await api.getRun(run.id)
        while (!isTerminal(detail.status) && mine === turn.current) {
          await new Promise((r) => setTimeout(r, POLL_MS))
          detail = await api.getRun(run.id)
        }
        window.clearTimeout(ack)
        if (mine !== turn.current) return

        if (detail.status === 'Failed' || !detail.finalAnswer) {
          add({ who: 'agent', text: detail.error ?? 'Something went wrong.', runId: run.id, failed: true })
          go('listening')
          return
        }
        add({ who: 'agent', text: detail.finalAnswer, runId: run.id })
        const audio = await api.speak(run.id, 'auto')
        player.current.stop() // the acknowledgement, if it is still playing
        await speakNow(audio, mine)
      } catch (e) {
        if (mine !== turn.current) return
        setError(
          e instanceof ApiError && e.status === 503
            ? 'Voice is unavailable right now. You can still type your question.'
            : 'Something went wrong with that turn. Try again.',
        )
        go('listening')
      }
    },
    [api, go, speakNow],
  )

  const turnOn = async () => {
    setError(null)
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
      setError('Microphone access was blocked. Allow it in the browser to talk to the agent.')
    }
  }

  const turnOff = useCallback(() => {
    turn.current++
    mic.current?.stop()
    mic.current = null
    player.current.stop()
    inputLevel.current = 0
    go('off')
  }, [go])

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

  return (
    <section className={`talk ${state}`} aria-label="Conversation">
      <div className="talkhead">
        <Avatar state={state} level={level} />
        <div className="talkside">
          <div className="talkstate" role="status" aria-live="polite" data-testid="conversation-state" data-state={state}>
            {STATUS[state]}
          </div>
          <button type="button" className={`btn mictoggle ${on ? 'on' : ''}`} aria-pressed={on} onClick={() => (on ? turnOff() : void turnOn())}>
            {on ? 'Microphone on' : 'Microphone off'}
          </button>
          <div className="row talkopts">
            <select
              aria-label="Conversation language"
              value={language}
              onChange={(e) => {
                const l = e.target.value as VoiceLanguage
                setLanguageState(l)
                saveLanguage(l)
              }}
            >
              <option value="sv">Svenska</option>
              <option value="en">English</option>
              <option value="auto">Auto</option>
            </select>
            <select aria-label="Microphone sensitivity" value={sensitivity} onChange={(e) => setSensitivity(e.target.value)}>
              <option value="low">Sensitivity: low</option>
              <option value="normal">Sensitivity: normal</option>
              <option value="high">Sensitivity: high</option>
            </select>
            {profiles.length > 1 && (
              <select aria-label="Profile" value={profile} onChange={(e) => setProfile(e.target.value)}>
                {profiles.map((p) => (
                  <option key={p.name} value={p.name}>
                    {p.name}
                  </option>
                ))}
              </select>
            )}
            <button
              type="button"
              className="btn"
              onClick={() => {
                conversationId.current = newId()
                setMessages([])
              }}
              disabled={messages.length === 0}
            >
              New conversation
            </button>
          </div>
          <p className="muted small">Headphones work best: the agent then never hears itself.</p>
          {error && (
            <p role="alert" className="error small">
              {error}
            </p>
          )}
        </div>
      </div>
      {messages.length > 0 && (
        <ol className="transcript" ref={log} aria-label="Conversation so far">
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
