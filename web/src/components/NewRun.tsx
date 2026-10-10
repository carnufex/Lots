import { useState } from 'react'
import type { Api, VoiceLanguage } from '../api'
import type { ProfileInfo, VoiceConfig } from '../config'
import MicButton from './MicButton'
import VocabularyEditor from './VocabularyEditor'

const LANGUAGE_KEY = 'lots.voice-language'

/** Remembered choice, else the browser language (a language hint is far more reliable than detecting it from a short clip). */
function initialLanguage(): VoiceLanguage {
  try {
    const saved = localStorage.getItem(LANGUAGE_KEY)
    if (saved === 'sv' || saved === 'en' || saved === 'auto') return saved
  } catch {
    /* ignore */
  }
  const l = navigator.language.toLowerCase()
  return l.startsWith('sv') ? 'sv' : l.startsWith('en') ? 'en' : 'auto'
}

export default function NewRun({ api, profiles, voice }: { api: Api; profiles: ProfileInfo[]; voice: VoiceConfig }) {
  const [prompt, setPrompt] = useState('')
  const [profile, setProfile] = useState(profiles[0]?.name ?? '')
  const [busy, setBusy] = useState(false)
  const [language, setLanguageState] = useState<VoiceLanguage>(initialLanguage)
  const setLanguage = (l: VoiceLanguage) => {
    setLanguageState(l)
    try {
      localStorage.setItem(LANGUAGE_KEY, l)
    } catch {
      /* a per-viewer convenience only */
    }
  }
  const [error, setError] = useState<string | null>(null)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!prompt.trim() || busy) return
    setBusy(true)
    setError(null)
    try {
      const run = await api.startRun(prompt.trim(), profile)
      window.location.hash = `#/runs/${run.id}`
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
      setBusy(false)
    }
  }

  return (
    <form className="newrun" onSubmit={(e) => void submit(e)}>
      <label className="sr" htmlFor="prompt">
        Question
      </label>
      <textarea
        id="prompt"
        rows={2}
        placeholder="Ask something, e.g. which containers are unhealthy?"
        value={prompt}
        onChange={(e) => setPrompt(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) void submit(e)
        }}
      />
      <div className="row">
        {profiles.length > 1 ? (
          <select aria-label="Profile" value={profile} onChange={(e) => setProfile(e.target.value)}>
            {profiles.map((p) => (
              <option key={p.name} value={p.name}>
                {p.name}
              </option>
            ))}
          </select>
        ) : (
          <span className="muted">{profile}</span>
        )}
        <span className="grow" />
        {voice.enabled && (
          <>
            <select aria-label="Speech language" value={language} onChange={(e) => setLanguage(e.target.value as VoiceLanguage)}>
              <option value="auto">Auto</option>
              <option value="sv">Svenska</option>
              <option value="en">English</option>
            </select>
            <MicButton api={api} language={language} onText={(t) => setPrompt((p) => (p.trim() ? `${p.trim()} ${t}` : t))} />
          </>
        )}
        {error && (
          <span role="alert" className="error">
            {error}
          </span>
        )}
        <button className="btn primary" type="submit" disabled={busy || !prompt.trim()}>
          {busy ? 'Starting…' : 'Start run'}
        </button>
      </div>
      {voice.enabled && <VocabularyEditor api={api} />}
    </form>
  )
}
